// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Memory.Tests;

/// <summary>
/// Exercises <see cref="SqliteSystemMessageStore"/> against a real SQLite file (ADR-0049 section 8): AND-combined filters with
/// inclusive bounds, case-insensitive text, a stable newest-first order, keyset pagination that neither skips nor repeats
/// when a newer message arrives, page bounds, compare-and-set prerequisite state, and survival across a restart.
/// </summary>
public sealed class SqliteSystemMessageStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"bops-system-messages-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Query_ReturnsNewestFirst_TiesBrokenByIdDescending()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        var older = Message(Start, id: Id(1));
        var tieLow = Message(Start.AddMinutes(1), id: Id(2));
        var tieHigh = Message(Start.AddMinutes(1), id: Id(3));
        var newest = Message(Start.AddMinutes(2), id: Id(4));
        foreach (var message in new[] { tieLow, newest, older, tieHigh })
        {
            await store.AppendAsync(message);
        }

        var page = await store.QueryAsync(new SystemMessageQuery());

        Assert.Equal([newest.Id, tieHigh.Id, tieLow.Id, older.Id], page.Items.Select(item => item.Id));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Message_RoundTripsEveryField()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        var message = Message(Start.ToOffset(TimeSpan.FromHours(2))) with
        {
            TaskId = Guid.NewGuid(),
            ComponentType = SystemComponentType.Tool,
            ComponentId = "sample.analyze",
            Metadata = OperationalMetadata.From(new JsonObject { ["affectedComponents"] = new JsonArray("tool:a", "tool:b"), ["count"] = 2, ["ok"] = false }),
        };

        await store.AppendAsync(message);
        var stored = Assert.Single((await store.QueryAsync(new SystemMessageQuery())).Items);

        Assert.Equal(message, stored);
        Assert.Equal(TimeSpan.Zero, stored.TimestampUtc.Offset);
    }

    [Fact]
    public async Task FromAndTo_AreInclusive()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        for (var minute = 0; minute < 5; minute++)
        {
            await store.AppendAsync(Message(Start.AddMinutes(minute), text: $"minute {minute}"));
        }

        var page = await store.QueryAsync(new SystemMessageQuery { FromUtc = Start.AddMinutes(1), ToUtc = Start.AddMinutes(3) });
        var exact = await store.QueryAsync(new SystemMessageQuery { FromUtc = Start.AddMinutes(2), ToUtc = Start.AddMinutes(2) });

        Assert.Equal(["minute 3", "minute 2", "minute 1"], page.Items.Select(item => item.Message));
        Assert.Equal("minute 2", Assert.Single(exact.Items).Message);
    }

    [Fact]
    public async Task Severity_IsAnExactMatch()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        await store.AppendAsync(Message(Start, SystemMessageSeverity.Information));
        await store.AppendAsync(Message(Start.AddSeconds(1), SystemMessageSeverity.Warning));
        await store.AppendAsync(Message(Start.AddSeconds(2), SystemMessageSeverity.Error));
        await store.AppendAsync(Message(Start.AddSeconds(3), SystemMessageSeverity.Critical));

        var warnings = await store.QueryAsync(new SystemMessageQuery { Severity = SystemMessageSeverity.Warning });

        Assert.Equal(SystemMessageSeverity.Warning, Assert.Single(warnings.Items).Severity);
    }

    [Fact]
    public async Task Text_IsContainedAndCaseInsensitive_IncludingNonAscii_AndNeverAPattern()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        await store.AppendAsync(Message(Start, text: "The Docker daemon is not reachable."));
        await store.AppendAsync(Message(Start.AddSeconds(1), text: "Verifica del servizio NON RIUSCITA: ÉCHEC."));
        await store.AppendAsync(Message(Start.AddSeconds(2), text: "Disk usage at 100% on volume C."));

        Assert.Single((await store.QueryAsync(new SystemMessageQuery { Text = "docker DAEMON" })).Items);
        Assert.Single((await store.QueryAsync(new SystemMessageQuery { Text = "échec" })).Items);
        Assert.Single((await store.QueryAsync(new SystemMessageQuery { Text = "100%" })).Items);
        Assert.Empty((await store.QueryAsync(new SystemMessageQuery { Text = "%" + "daemon" + "_" })).Items);
        Assert.Empty((await store.QueryAsync(new SystemMessageQuery { Text = "absent" })).Items);
    }

    [Fact]
    public async Task AllFilters_CombineWithAnd()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        var match = Message(Start.AddMinutes(5), SystemMessageSeverity.Warning, "Docker daemon unavailable.");
        await store.AppendAsync(match);
        await store.AppendAsync(Message(Start.AddMinutes(5), SystemMessageSeverity.Error, "Docker daemon unavailable."));
        await store.AppendAsync(Message(Start.AddMinutes(6), SystemMessageSeverity.Warning, "Search endpoint unavailable."));
        await store.AppendAsync(Message(Start.AddMinutes(20), SystemMessageSeverity.Warning, "Docker daemon unavailable."));

        var page = await store.QueryAsync(new SystemMessageQuery
        {
            FromUtc = Start,
            ToUtc = Start.AddMinutes(10),
            Severity = SystemMessageSeverity.Warning,
            Text = "docker",
        });

        Assert.Equal(match.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task CursorPagination_NeitherSkipsNorRepeats_WhenANewerMessageArrivesBetweenPages()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        var originals = new List<SystemMessage>();
        for (var i = 0; i < 7; i++)
        {
            // Two messages share every timestamp, so page boundaries also fall inside a tie.
            var message = Message(Start.AddMinutes(i / 2), text: $"message {i}");
            originals.Add(message);
            await store.AppendAsync(message);
        }

        var first = await store.QueryAsync(new SystemMessageQuery { PageSize = 3 });
        await store.AppendAsync(Message(Start.AddHours(1), text: "newer"));
        var second = await store.QueryAsync(new SystemMessageQuery { PageSize = 3, Cursor = first.NextCursor });
        var third = await store.QueryAsync(new SystemMessageQuery { PageSize = 3, Cursor = second.NextCursor });

        var seen = first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id).ToArray();
        var expected = originals.OrderByDescending(item => item.TimestampUtc).ThenByDescending(item => item.Id.ToString("D"), StringComparer.Ordinal).Select(item => item.Id).ToArray();
        Assert.Equal(expected, seen);
        Assert.Equal([3, 3, 1], new[] { first.Items.Count, second.Items.Count, third.Items.Count });
        Assert.Null(third.NextCursor);
        Assert.Equal("newer", (await store.QueryAsync(new SystemMessageQuery { PageSize = 1 })).Items[0].Message);
    }

    [Fact]
    public async Task Cursor_KeepsTheFilters_ItIsCombinedWith()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        for (var i = 0; i < 6; i++)
        {
            await store.AppendAsync(Message(Start.AddMinutes(i), i % 2 == 0 ? SystemMessageSeverity.Warning : SystemMessageSeverity.Information));
        }

        var query = new SystemMessageQuery { Severity = SystemMessageSeverity.Warning, PageSize = 2 };
        var first = await store.QueryAsync(query);
        var second = await store.QueryAsync(query with { Cursor = first.NextCursor });

        Assert.Equal(2, first.Items.Count);
        Assert.All(second.Items, item => Assert.Equal(SystemMessageSeverity.Warning, item.Severity));
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task PageSize_IsBounded_AndTheCursorMustBeValid()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        for (var i = 0; i < 205; i++)
        {
            await store.AppendAsync(Message(Start.AddSeconds(i)));
        }

        Assert.Equal(SystemMessageQuery.DefaultPageSize, (await store.QueryAsync(new SystemMessageQuery())).Items.Count);
        Assert.Equal(200, (await store.QueryAsync(new SystemMessageQuery { PageSize = 200 })).Items.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryAsync(new SystemMessageQuery { PageSize = 201 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryAsync(new SystemMessageQuery { PageSize = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(new SystemMessageQuery { Cursor = "not-a-cursor" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync(new SystemMessageQuery { Cursor = new string('A', 4096) }));
    }

    [Fact]
    public async Task Append_RejectsInvalidAndDuplicateMessages()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        var message = Message(Start);
        await store.AppendAsync(message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(message));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(Message(Start) with { Code = "Bad Code" }));
    }

    [Fact]
    public async Task Purge_DeletesOnlyStrictlyOlderMessages()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        await store.AppendAsync(Message(Start.AddDays(-91)));
        await store.AppendAsync(Message(Start.AddDays(-90)));
        await store.AppendAsync(Message(Start));

        var deleted = await store.PurgeOlderThanAsync(Start - SystemMessageRetention.Default);

        Assert.Equal(1, deleted);
        Assert.Equal(2, (await store.QueryAsync(new SystemMessageQuery())).Items.Count);
    }

    [Fact]
    public async Task SaveState_IsCompareAndSet_AndWritesStateAndMessageTogether()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        var missing = State(PrerequisiteState.Unavailable, "executable-not-found");
        var transition = Message(Start, SystemMessageSeverity.Warning);

        Assert.True(await store.SaveStateAsync(missing, expectedFingerprint: null, transition));
        Assert.False(await store.SaveStateAsync(missing, expectedFingerprint: null, Message(Start.AddSeconds(1))));
        Assert.False(await store.SaveStateAsync(missing, "Available|available", Message(Start.AddSeconds(2))));
        Assert.True(await store.SaveStateAsync(missing with { CheckedAtUtc = Start.AddMinutes(1) }, missing.Fingerprint, transitionMessage: null));

        Assert.Equal(transition.Id, Assert.Single((await store.QueryAsync(new SystemMessageQuery())).Items).Id);
        Assert.Equal(Start.AddMinutes(1), (await store.LoadStateAsync(NodeId.Local, "sample.debugger"))!.CheckedAtUtc);
    }

    [Fact]
    public async Task SaveState_WithAnInvalidMessage_WritesNothing()
    {
        var store = new SqliteSystemMessageStore(_filePath);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveStateAsync(State(PrerequisiteState.Unavailable, "missing"), null, Message(Start) with { Source = "bad" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveStateAsync(State(PrerequisiteState.Unknown, "missing"), null, transitionMessage: null));

        Assert.Null(await store.LoadStateAsync(NodeId.Local, "sample.debugger"));
        Assert.Empty((await store.QueryAsync(new SystemMessageQuery())).Items);
    }

    [Fact]
    public async Task States_AreScopedByNode_AndListedById()
    {
        var store = new SqliteSystemMessageStore(_filePath);
        await store.SaveStateAsync(State(PrerequisiteState.Available, "available", "sample.z"), null, null);
        await store.SaveStateAsync(State(PrerequisiteState.Available, "available", "sample.a"), null, null);
        await store.SaveStateAsync(State(PrerequisiteState.Available, "available", "sample.a") with { Node = new NodeId("node-2") }, null, null);

        Assert.Equal(["sample.a", "sample.z"], (await store.ListStatesAsync(NodeId.Local)).Select(state => state.PrerequisiteId));
        Assert.Single(await store.ListStatesAsync(new NodeId("node-2")));
    }

    [Fact]
    public async Task Restart_PreservesPrerequisiteStateAndMessages()
    {
        var state = State(PrerequisiteState.Unavailable, "executable-not-found") with
        {
            Metadata = OperationalMetadata.From(new JsonObject { ["requiredExecutable"] = "kd.exe" }),
        };
        var message = Message(Start, SystemMessageSeverity.Warning);
        await new SqliteSystemMessageStore(_filePath).SaveStateAsync(state, null, message);
        SqliteConnection.ClearAllPools();

        var reopened = new SqliteSystemMessageStore(_filePath);

        Assert.Equal(state, await reopened.LoadStateAsync(NodeId.Local, "sample.debugger"));
        Assert.Equal(message, Assert.Single((await reopened.QueryAsync(new SystemMessageQuery())).Items));
        Assert.False(await reopened.SaveStateAsync(state, expectedFingerprint: null, Message(Start.AddSeconds(1))));
    }

    [Fact]
    public async Task ANewerSchemaVersion_IsRefused()
    {
        _ = new SqliteSystemMessageStore(_filePath);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        Assert.Throws<InvalidOperationException>(() => new SqliteSystemMessageStore(_filePath));
        Assert.Equal(1, SqliteSystemMessageStore.SchemaVersion);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _filePath, _filePath + "-wal", _filePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static Guid Id(int value) => new($"00000000-0000-7000-8000-{value:000000000000}");

    private static SystemMessage Message(
        DateTimeOffset at, SystemMessageSeverity severity = SystemMessageSeverity.Information, string text = "A system message.", Guid? id = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(at),
        TimestampUtc = at,
        Node = NodeId.Local,
        Source = "prerequisite/sample.debugger",
        Severity = severity,
        Code = "prerequisite.missing",
        Message = text,
    };

    private static PrerequisiteStateRecord State(PrerequisiteState state, string code, string id = "sample.debugger") => new()
    {
        Node = NodeId.Local,
        PrerequisiteId = id,
        State = state,
        Code = code,
        Message = "The sample debugger is required.",
        CheckedAtUtc = Start,
        ChangedAtUtc = Start,
    };
}
