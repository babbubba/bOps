// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Memory;

/// <summary>
/// <see cref="ITaskStore"/> backed by a single-table SQLite database: one row per task, the
/// whole <see cref="TaskState"/> serialized as a JSON column (V0.7, ADR-0017). Deliberately not
/// EF Core — there is exactly one aggregate with no relational queries beyond "by id" and "by
/// status," so a parameterized upsert and a JSON column are the entire feature, not a simplified
/// version of a larger one.
///
/// Every connection opens in WAL journal mode with a busy timeout (V0.9, ADR-0018): SQLite's
/// default journal mode blocks a reader behind an in-progress writer and, with no busy timeout
/// set, fails that read immediately with <c>SQLITE_BUSY</c> instead of waiting briefly. That was
/// invisible while <c>bOps.Cli</c> was the only consumer — one process, one task, no concurrent
/// access to the same file — but <c>bOps.Api</c> writes a task's <c>Running</c> snapshot from a
/// detached background operation while an HTTP request can read the same task at the same moment,
/// which is exactly the access pattern WAL mode plus a busy timeout exists to make safe.
///
/// From ADR-0051 the same file holds a second table, <c>task_mutation_journal</c>: the durable intent and outcome of every
/// side-effecting call of an ordinary task. Every journal write is one statement or one <c>BEGIN IMMEDIATE</c> transaction,
/// fenced on the task row inside that statement or transaction, on a connection with <c>synchronous=FULL</c>; nothing is
/// ever loaded, checked in process and saved.
/// </summary>
public sealed class SqliteTaskStore : ITaskStore, ITaskMutationJournalStore
{
    /// <summary>The journal states that block an acquisition (ADR-0051 §6.3): unsettled, or abandoned.</summary>
    private const string BlockingStates = "('Pending', 'Ambiguous', 'Escalated', 'Abandoned')";

    private readonly string _connectionString;
    private readonly string _filePath;

    /// <summary>Opens (creating if needed) a SQLite-backed task store at <paramref name="filePath"/>.</summary>
    public SqliteTaskStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _filePath = Path.GetFullPath(filePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Fenced by execution attempt (ADR-0040 §4.1): a save whose <see cref="TaskState.ExecutionAttempt"/> is lower than the
    /// stored one writes nothing and throws <see cref="TaskExecutionSupersededException"/>.
    /// </remarks>
    public async Task SaveAsync(TaskState task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tasks (id, status, execution_attempt, updated_at_utc, state_json)
            VALUES ($id, $status, $executionAttempt, $updatedAtUtc, $stateJson)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                execution_attempt = excluded.execution_attempt,
                updated_at_utc = excluded.updated_at_utc,
                state_json = excluded.state_json
            WHERE tasks.execution_attempt <= excluded.execution_attempt;
            """;
        AddStateParameters(command, task);

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            throw new TaskExecutionSupersededException(task.Id, task.ExecutionAttempt);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(TaskState task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tasks (id, status, execution_attempt, updated_at_utc, state_json)
            VALUES ($id, $status, $executionAttempt, $updatedAtUtc, $stateJson)
            ON CONFLICT(id) DO NOTHING;
            """;
        AddStateParameters(command, task);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One conditional <c>UPDATE</c>: the comparison and the write are a single statement, and SQLite serializes writers on
    /// the database file, so no other thread or process sharing the file can write between them (ADR-0040 §4.2). An
    /// acquisition (one execution attempt more) is also refused, in the same statement, while the task has a pending,
    /// ambiguous, escalated or abandoned journal entry (ADR-0051 §6.3, defence in depth).
    /// </remarks>
    public async Task<bool> TryTransitionAsync(
        TaskState task, AgentTaskStatus expectedStatus, int expectedExecutionAttempt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.ExecutionAttempt != expectedExecutionAttempt && task.ExecutionAttempt != expectedExecutionAttempt + 1)
        {
            throw new ArgumentException(
                $"A transition from execution attempt {expectedExecutionAttempt} may write attempt {expectedExecutionAttempt} or {expectedExecutionAttempt + 1}, not {task.ExecutionAttempt}.",
                nameof(task));
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tasks SET
                status = $status,
                execution_attempt = $executionAttempt,
                updated_at_utc = $updatedAtUtc,
                state_json = $stateJson
            WHERE id = $id AND status = $expectedStatus AND execution_attempt = $expectedExecutionAttempt
              AND ($executionAttempt = $expectedExecutionAttempt OR NOT EXISTS (
                  SELECT 1 FROM task_mutation_journal WHERE task_id = $id AND state IN
            """ + BlockingStates + "));";
        AddStateParameters(command, task);
        command.Parameters.AddWithValue("$expectedStatus", expectedStatus.ToString());
        command.Parameters.AddWithValue("$expectedExecutionAttempt", expectedExecutionAttempt);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One <c>INSERT … SELECT … FROM tasks WHERE status/attempt</c>: the fence, the free key and the next sequence are decided
    /// by the statement that writes (ADR-0051 §6.3).
    /// </remarks>
    public async Task<bool> TryRecordIntentAsync(TaskMutationIntent intent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        using var connection = await OpenJournalConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO task_mutation_journal (
                task_id, execution_attempt, step_index, sequence, tool_name, arguments_hash, risk, verification_tool_name,
                plan_revision, planned_step_index, intent_at_utc, state)
            SELECT $taskId, $executionAttempt, $stepIndex,
                   COALESCE((SELECT MAX(sequence) FROM task_mutation_journal WHERE task_id = $taskId), 0) + 1,
                   $toolName, $argumentsHash, $risk, $verificationToolName, $planRevision, $plannedStepIndex, $intentAtUtc, 'Pending'
            FROM tasks
            WHERE id = $taskId AND status = 'Running' AND execution_attempt = $executionAttempt
            ON CONFLICT DO NOTHING;
            """;
        AddKeyParameters(command, intent.Key);
        command.Parameters.AddWithValue("$toolName", intent.ToolName);
        command.Parameters.AddWithValue("$argumentsHash", intent.ArgumentsHash);
        command.Parameters.AddWithValue("$risk", intent.Risk.ToString());
        command.Parameters.AddWithValue("$verificationToolName", (object?)intent.VerificationToolName ?? DBNull.Value);
        command.Parameters.AddWithValue("$planRevision", DbValue(intent.PlanRevision));
        command.Parameters.AddWithValue("$plannedStepIndex", DbValue(intent.PlannedStepIndex));
        command.Parameters.AddWithValue("$intentAtUtc", Timestamp(intent.IntentAtUtc));

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One <c>BEGIN IMMEDIATE</c> transaction: the entry update and the task-row replacement are both fenced on
    /// <c>(Running, attempt)</c>, and either both commit or neither does.
    /// </remarks>
    public async Task<bool> TryRecordOutcomeAsync(
        TaskMutationKey key, TaskMutationOutcome outcome, TaskMutationState state, TaskState owningTask, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(owningTask);
        if (owningTask.Id != key.TaskId || owningTask.ExecutionAttempt != key.ExecutionAttempt || owningTask.Status != AgentTaskStatus.Running)
        {
            throw new ArgumentException(
                "The owning task must be the Running state of the outcome's own task and execution attempt.", nameof(owningTask));
        }

        using var connection = await OpenJournalConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = await BeginImmediateAsync(connection, ct).ConfigureAwait(false);

        using (var entry = connection.CreateCommand())
        {
            entry.Transaction = transaction;
            entry.CommandText =
                """
                UPDATE task_mutation_journal SET
                    outcome_kind = $outcomeKind,
                    outcome_tool_outcome = $toolOutcome,
                    outcome_failure_kind = $failureKind,
                    outcome_verification = $verification,
                    outcome_at_utc = $outcomeAtUtc,
                    state = $state
                WHERE task_id = $taskId AND execution_attempt = $executionAttempt AND step_index = $stepIndex
                  AND outcome_kind IS NULL AND reconciliation_json IS NULL
                  AND EXISTS (SELECT 1 FROM tasks WHERE id = $taskId AND status = 'Running' AND execution_attempt = $executionAttempt);
                """;
            AddKeyParameters(entry, key);
            entry.Parameters.AddWithValue("$outcomeKind", outcome.Kind.ToString());
            entry.Parameters.AddWithValue("$toolOutcome", (object?)outcome.ToolOutcome?.ToString() ?? DBNull.Value);
            entry.Parameters.AddWithValue("$failureKind", (object?)outcome.ToolFailureKind?.ToString() ?? DBNull.Value);
            entry.Parameters.AddWithValue("$verification", (object?)outcome.Verification?.ToString() ?? DBNull.Value);
            entry.Parameters.AddWithValue("$outcomeAtUtc", Timestamp(outcome.AtUtc));
            entry.Parameters.AddWithValue("$state", state.ToString());
            if (await entry.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }

        using (var task = connection.CreateCommand())
        {
            task.Transaction = transaction;
            task.CommandText =
                """
                UPDATE tasks SET
                    status = $status,
                    execution_attempt = $executionAttempt,
                    updated_at_utc = $updatedAtUtc,
                    state_json = $stateJson
                WHERE id = $id AND status = 'Running' AND execution_attempt = $executionAttempt;
                """;
            AddStateParameters(task, owningTask);
            if (await task.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>One deferred read transaction: the task row and its entries come from the same snapshot.</remarks>
    public async Task<TaskJournalSnapshot?> LoadWithJournalAsync(Guid taskId, CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        // A deferred BEGIN takes no write lock; in WAL mode its first read fixes the snapshot every later read sees.
        await ReadSnapshotAsync(connection, begin: true, ct).ConfigureAwait(false);
        try
        {
            TaskState? task;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT state_json FROM tasks WHERE id = $id;";
                command.Parameters.AddWithValue("$id", taskId.ToString());
                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                task = result is string json ? JsonSerializer.Deserialize(json, MemoryJsonContext.Default.TaskState) : null;
            }

            if (task is null)
            {
                return null;
            }

            var entries = new List<TaskMutationJournalEntry>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT execution_attempt, step_index, sequence, tool_name, arguments_hash, risk, verification_tool_name,
                           plan_revision, planned_step_index, intent_at_utc, outcome_kind, outcome_tool_outcome, outcome_failure_kind,
                           outcome_verification, outcome_at_utc, state, reconciliation_json, history_recorded_in_attempt
                    FROM task_mutation_journal WHERE task_id = $taskId ORDER BY sequence;
                    """;
                command.Parameters.AddWithValue("$taskId", taskId.ToString());
                using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    entries.Add(ReadEntry(taskId, reader));
                }
            }

            return new TaskJournalSnapshot(task, entries);
        }
        finally
        {
            await ReadSnapshotAsync(connection, begin: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One <c>BEGIN IMMEDIATE</c> transaction: the task-row check and every per-entry compare-and-set are decided while the
    /// write lock is held.
    /// </remarks>
    public async Task<bool> TryReconcileAsync(Guid taskId, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        IReadOnlyList<TaskMutationResolution> resolutions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        if (resolutions.Count == 0 || resolutions.Any(resolution => resolution.Key.TaskId != taskId))
        {
            throw new ArgumentException("A reconciliation names at least one entry, all of the given task.", nameof(resolutions));
        }

        if (expectedStatus == AgentTaskStatus.Running)
        {
            return false;
        }

        using var connection = await OpenJournalConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = await BeginImmediateAsync(connection, ct).ConfigureAwait(false);

        if (!await TaskRowIsAsync(connection, transaction, taskId, expectedStatus, expectedExecutionAttempt, ct).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        foreach (var resolution in resolutions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE task_mutation_journal SET state = $newState, reconciliation_json = $reconciliation
                WHERE task_id = $taskId AND execution_attempt = $executionAttempt AND step_index = $stepIndex
                  AND state = $expectedState;
                """;
            AddKeyParameters(command, resolution.Key);
            command.Parameters.AddWithValue("$newState", resolution.NewState.ToString());
            command.Parameters.AddWithValue("$expectedState", resolution.ExpectedState.ToString());
            command.Parameters.AddWithValue("$reconciliation",
                JsonSerializer.Serialize(resolution.Reconciliation, MemoryJsonContext.Default.StepReconciliation));
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One <c>BEGIN IMMEDIATE</c> transaction: the blocking-entry check, the history marks and the task-row transition commit
    /// together or not at all.
    /// </remarks>
    public async Task<bool> TryAcquireAsync(TaskState acquired, AgentTaskStatus expectedStatus, int expectedExecutionAttempt,
        IReadOnlyList<TaskMutationKey> recordedInHistory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(acquired);
        ArgumentNullException.ThrowIfNull(recordedInHistory);
        if (acquired.ExecutionAttempt != expectedExecutionAttempt + 1 || acquired.Status != AgentTaskStatus.Running)
        {
            throw new ArgumentException("An acquisition writes the Running state of the next execution attempt.", nameof(acquired));
        }

        if (recordedInHistory.Any(key => key.TaskId != acquired.Id))
        {
            throw new ArgumentException("Every recorded entry must belong to the acquired task.", nameof(recordedInHistory));
        }

        using var connection = await OpenJournalConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = await BeginImmediateAsync(connection, ct).ConfigureAwait(false);

        using (var blocking = connection.CreateCommand())
        {
            blocking.Transaction = transaction;
            blocking.CommandText = "SELECT COUNT(*) FROM task_mutation_journal WHERE task_id = $taskId AND state IN " + BlockingStates + ";";
            blocking.Parameters.AddWithValue("$taskId", acquired.Id.ToString());
            if (Convert.ToInt64(await blocking.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }

        foreach (var key in recordedInHistory)
        {
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText =
                """
                UPDATE task_mutation_journal SET history_recorded_in_attempt = $acquiredAttempt
                WHERE task_id = $taskId AND execution_attempt = $executionAttempt AND step_index = $stepIndex
                  AND state = 'ReconciledDone' AND history_recorded_in_attempt IS NULL;
                """;
            AddKeyParameters(mark, key);
            mark.Parameters.AddWithValue("$acquiredAttempt", acquired.ExecutionAttempt);
            if (await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }

        using (var task = connection.CreateCommand())
        {
            task.Transaction = transaction;
            task.CommandText =
                """
                UPDATE tasks SET
                    status = $status,
                    execution_attempt = $executionAttempt,
                    updated_at_utc = $updatedAtUtc,
                    state_json = $stateJson
                WHERE id = $id AND status = $expectedStatus AND execution_attempt = $expectedExecutionAttempt;
                """;
            AddStateParameters(task, acquired);
            task.Parameters.AddWithValue("$expectedStatus", expectedStatus.ToString());
            task.Parameters.AddWithValue("$expectedExecutionAttempt", expectedExecutionAttempt);
            if (await task.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static void AddStateParameters(SqliteCommand command, TaskState task)
    {
        command.Parameters.AddWithValue("$id", task.Id.ToString());
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$executionAttempt", task.ExecutionAttempt);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$stateJson", JsonSerializer.Serialize(task, MemoryJsonContext.Default.TaskState));
    }

    /// <inheritdoc />
    public async Task<TaskState?> LoadAsync(Guid taskId, CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", taskId.ToString());

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string json ? JsonSerializer.Deserialize(json, MemoryJsonContext.Default.TaskState) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskState>> ListByStatusAsync(AgentTaskStatus status, CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM tasks WHERE status = $status ORDER BY updated_at_utc;";
        command.Parameters.AddWithValue("$status", status.ToString());

        var results = new List<TaskState>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize(reader.GetString(0), MemoryJsonContext.Default.TaskState) is { } state)
            {
                results.Add(state);
            }
        }

        return results;
    }

    /// <summary>
    /// A connection for a journal write: the store's busy timeout, and <c>synchronous=FULL</c> so a commit that returned survives
    /// power loss, not only process death (ADR-0051 §6.2). Like <c>busy_timeout</c>, <c>synchronous</c> is per connection.
    /// </summary>
    internal async Task<SqliteConnection> OpenJournalConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SetBusyTimeoutAsync(connection, ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts a write transaction that takes SQLite's write lock at once (<c>BEGIN IMMEDIATE</c>), so every check inside it is decided under that lock.</summary>
    /// <remarks>Microsoft.Data.Sqlite begins every transaction it is not told to defer with <c>BEGIN IMMEDIATE</c>.</remarks>
    private static async Task<SqliteTransaction> BeginImmediateAsync(SqliteConnection connection, CancellationToken ct) =>
        (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

    /// <summary>Begins or ends a deferred read transaction, which takes no write lock.</summary>
    private static async Task ReadSnapshotAsync(SqliteConnection connection, bool begin, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        if (begin)
        {
            command.CommandText = "BEGIN DEFERRED;";
        }
        else
        {
            command.CommandText = "COMMIT;";
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static object DbValue(int? value) => value.HasValue ? value.Value : DBNull.Value;

    private static async Task<bool> TaskRowIsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid taskId,
        AgentTaskStatus status, int executionAttempt, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM tasks WHERE id = $id AND status = $status AND execution_attempt = $executionAttempt;";
        command.Parameters.AddWithValue("$id", taskId.ToString());
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$executionAttempt", executionAttempt);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private static void AddKeyParameters(SqliteCommand command, TaskMutationKey key)
    {
        command.Parameters.AddWithValue("$taskId", key.TaskId.ToString());
        command.Parameters.AddWithValue("$executionAttempt", key.ExecutionAttempt);
        command.Parameters.AddWithValue("$stepIndex", key.StepIndex);
    }

    private static TaskMutationJournalEntry ReadEntry(Guid taskId, SqliteDataReader reader)
    {
        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        int? Number(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        T? Named<T>(int ordinal) where T : struct, Enum => Text(ordinal) is { } name ? Enum.Parse<T>(name) : null;

        var outcomeKind = Named<MutationOutcomeKind>(10);
        return new TaskMutationJournalEntry
        {
            Intent = new TaskMutationIntent
            {
                Key = new TaskMutationKey(taskId, reader.GetInt32(0), reader.GetInt32(1)),
                ToolName = reader.GetString(3),
                ArgumentsHash = reader.GetString(4),
                Risk = Enum.Parse<RiskLevel>(reader.GetString(5)),
                VerificationToolName = Text(6),
                PlanRevision = Number(7),
                PlannedStepIndex = Number(8),
                IntentAtUtc = ParseTimestamp(reader.GetString(9)),
            },
            Sequence = reader.GetInt32(2),
            Outcome = outcomeKind is { } kind
                ? new TaskMutationOutcome(kind, Named<ToolOutcome>(11), Named<ToolFailureKind>(12), Named<VerificationStatus>(13),
                    ParseTimestamp(reader.GetString(14)))
                : null,
            State = Enum.Parse<TaskMutationState>(reader.GetString(15)),
            Reconciliation = Text(16) is { } reconciliation
                ? JsonSerializer.Deserialize(reconciliation, MemoryJsonContext.Default.StepReconciliation)
                : null,
            HistoryRecordedInAttempt = Number(17),
        };
    }

    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var busyCommand = connection.CreateCommand())
        {
            // The schema migration below can meet another process opening the same file.
            busyCommand.CommandText = "PRAGMA busy_timeout=5000;";
            busyCommand.ExecuteNonQuery();
        }

        // WAL is a durable, once-per-file setting (persisted in the database itself), so it only
        // needs setting here — every later connection this store opens inherits it automatically.
        using (var walCommand = connection.CreateCommand())
        {
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            walCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                state_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_status ON tasks(status);
            """;
        command.ExecuteNonQuery();

        EnsureExecutionAttemptColumn(connection);
        EnsureMutationJournalTable(connection);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// The ADR-0040 §4.2 migration, additive and idempotent: <c>execution_attempt</c> mirrors
    /// <see cref="TaskState.ExecutionAttempt"/> so a transition can compare it in the same statement that writes. Existing
    /// rows get 1, which is what their JSON loads as; nothing is rewritten, dropped or recreated. A process that loses the
    /// race to add the column sees "duplicate column" and re-checks.
    /// </summary>
    private static void EnsureExecutionAttemptColumn(SqliteConnection connection)
    {
        if (HasExecutionAttemptColumn(connection))
        {
            return;
        }

        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE tasks ADD COLUMN execution_attempt INTEGER NOT NULL DEFAULT 1;";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) when (HasExecutionAttemptColumn(connection))
        {
            // Another process sharing the file added it first.
        }
    }

    /// <summary>
    /// The ADR-0051 §6.2 migration, additive and idempotent: one new table and its index. Nothing in <c>tasks</c> is changed,
    /// dropped or rewritten; enum values are stored by name, like <c>tasks.status</c>.
    /// </summary>
    private static void EnsureMutationJournalTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS task_mutation_journal (
                task_id                      TEXT    NOT NULL,
                execution_attempt            INTEGER NOT NULL,
                step_index                   INTEGER NOT NULL,
                sequence                     INTEGER NOT NULL,
                tool_name                    TEXT    NOT NULL,
                arguments_hash               TEXT    NOT NULL,
                risk                         TEXT    NOT NULL,
                verification_tool_name       TEXT    NULL,
                plan_revision                INTEGER NULL,
                planned_step_index           INTEGER NULL,
                intent_at_utc                TEXT    NOT NULL,
                outcome_kind                 TEXT    NULL,
                outcome_tool_outcome         TEXT    NULL,
                outcome_failure_kind         TEXT    NULL,
                outcome_verification         TEXT    NULL,
                outcome_at_utc               TEXT    NULL,
                state                        TEXT    NOT NULL,
                reconciliation_json          TEXT    NULL,
                history_recorded_in_attempt  INTEGER NULL,
                PRIMARY KEY (task_id, execution_attempt, step_index),
                UNIQUE (task_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_task_mutation_journal_state ON task_mutation_journal(task_id, state);
            """;
        command.ExecuteNonQuery();
    }

    private static bool HasExecutionAttemptColumn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(tasks);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "execution_attempt", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Unlike WAL mode, <c>busy_timeout</c> is a per-connection setting — it must be set again on
    /// every new <see cref="SqliteConnection"/> this store opens, immediately after opening it.
    /// </summary>
    private static async Task SetBusyTimeoutAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
