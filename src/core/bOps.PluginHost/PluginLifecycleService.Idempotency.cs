// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;

namespace bOps.PluginHost;

/// <summary>
/// Durable actor/node/key idempotency (ADR-0037). The uniqueness scope is exactly
/// (NodeId, ActorIdentity, IdempotencyKey): plugin id and operation kind are intent, never scope.
/// A record is reserved before any archive parsing or plugin identity discovery; only its owner runs the
/// mutation, and every same-scope request joins it, replays its result, or conflicts.
/// </summary>
public sealed partial class PluginLifecycleService
{
    private readonly object _reservationGate = new();
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Number of scoped idempotency keys with an owner currently executing.</summary>
    internal int InFlightReservationCount
    {
        get
        {
            lock (_reservationGate)
            {
                return _inFlight.Count;
            }
        }
    }

    private async Task<PluginLifecycleResult> ExecuteAsync(
        PluginLifecycleRequestContext context,
        Intent intent,
        Stream? archive,
        Func<InFlight?, CancellationToken, Task<PluginLifecycleResult>> owner,
        CancellationToken cancellationToken)
    {
        var key = context.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return await RunOwnerAsync(context, null, owner, cancellationToken);
        }

        if (key.Length > MaximumIdempotencyKeyLength)
        {
            throw new ArgumentException($"Idempotency-Key must not exceed {MaximumIdempotencyKeyLength} characters.", nameof(context));
        }

        var scope = $"{context.Node.Value}\n{context.Actor.Kind}\n{context.Actor.Id}\n{key}";
        string? ownDigest = null;
        while (true)
        {
            var (mine, follower, durable) = Reserve(context, scope, intent);
            if (mine is not null)
            {
                return await RunOwnerAsync(context, mine, owner, cancellationToken);
            }

            if (follower is not null && !KnownFieldsMatch(follower.Known, intent))
            {
                return await ConflictAsync(context, intent);
            }

            if (archive is not null && ownDigest is null)
            {
                ownDigest = await DigestBoundedAsync(archive, cancellationToken);
                if (ownDigest is null)
                {
                    return await Finish(context, intent.Operation, "archive", new PluginLifecycleResult(PluginLifecycleResultCategory.ArchiveLimitExceeded, null, null, null, 0, "archive"), null);
                }
            }

            var ownIntent = intent with { ArchiveDigest = ownDigest };
            if (durable is not null)
            {
                if (!string.Equals(Fingerprint(ownIntent), durable.IntentFingerprint, StringComparison.Ordinal))
                {
                    return await ConflictAsync(context, intent);
                }

                var replay = ReplayOf(durable);
                await AuditAsync(context, intent.Operation, "idempotency", replay.Category.ToString(), replay.PluginId, replay.PluginVersion, null, replay.State, replay.LifecycleVersion, null, replay: true);
                return replay;
            }

            // Join the in-flight owner: never start a second mutation.
            await follower!.DigestBound.Task.WaitAsync(cancellationToken);
            if (follower.Digest is not null && !string.Equals(follower.Digest, ownDigest, StringComparison.Ordinal))
            {
                return await ConflictAsync(context, intent);
            }

            var outcome = await follower.Done.Task.WaitAsync(cancellationToken);
            if (outcome.Released)
            {
                // The owner never bound an intent (it failed before its digest was known); nothing was mutated.
                continue;
            }

            var joined = outcome.Result! with { Replayed = true };
            await AuditAsync(context, intent.Operation, "idempotency", joined.Category.ToString(), joined.PluginId, joined.PluginVersion, null, joined.State, joined.LifecycleVersion, null, replay: true);
            return joined;
        }
    }

    private async Task<PluginLifecycleResult> ConflictAsync(PluginLifecycleRequestContext context, Intent intent)
    {
        var conflict = new PluginLifecycleResult(PluginLifecycleResultCategory.IdempotencyConflict, null, null, null, 0, "idempotency");
        await AuditAsync(context, intent.Operation, "idempotency", nameof(PluginLifecycleResultCategory.IdempotencyConflict), null, null, null, null, 0, null, replay: false);
        return conflict;
    }

    private (InFlight? Owner, InFlight? Follower, PluginIdempotencyOperation? Durable) Reserve(
        PluginLifecycleRequestContext context, string scope, Intent intent)
    {
        lock (_reservationGate)
        {
            if (_inFlight.TryGetValue(scope, out var existing))
            {
                return (null, existing, null);
            }

            var now = _manager.Clock.GetUtcNow();
            var document = _store.Read();
            var record = document.Operations.FirstOrDefault(item => SameScope(item, context));
            if (record is { Status: PluginIdempotencyStatus.Completed } && record.ExpiresAtUtc > now)
            {
                return (null, null, record);
            }

            var flight = new InFlight(scope, intent);
            _inFlight[scope] = flight;
            var fingerprint = intent.Operation == "install" ? null : Fingerprint(intent);
            if (fingerprint is not null)
            {
                flight.BindDigest(null);
            }

            _store.Mutate(d =>
            {
                d.Operations.RemoveAll(item => SameScope(item, context) || item.ExpiresAtUtc <= now);
                while (d.Operations.Count >= MaximumIdempotencyRecords)
                {
                    d.Operations.RemoveAt(0);
                }

                d.Operations.Add(new PluginIdempotencyOperation(
                    context.Node.Value, context.Actor.Kind, context.Actor.Id, context.IdempotencyKey!, PluginIdempotencyStatus.InProgress,
                    fingerprint, null, now + IdempotencyRetention, intent.Operation, intent.PluginId));
            });
            return (flight, null, null);
        }
    }

    private async Task<PluginLifecycleResult> RunOwnerAsync(
        PluginLifecycleRequestContext context,
        InFlight? flight,
        Func<InFlight?, CancellationToken, Task<PluginLifecycleResult>> owner,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inFlightMutations);
        PluginLifecycleResult result;
        try
        {
            await Hook("Reserved");
            result = await owner(flight, cancellationToken);
        }
        catch (PluginLifecycleInterruptedException)
        {
            // Simulated process death: leave every durable and in-memory marker exactly as a crash would.
            throw;
        }
        catch
        {
            Release(context, flight);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightMutations);
        }

        if (flight is not null)
        {
            Complete(context, flight, result);
        }

        return result;
    }

    /// <summary>Binds the received archive digest to the owner's reservation (request identity only).</summary>
    private Task BindDigestAsync(PluginLifecycleRequestContext context, InFlight? flight, string digest)
    {
        if (flight is null)
        {
            return Task.CompletedTask;
        }

        var fingerprint = Fingerprint(flight.Known with { ArchiveDigest = digest });
        lock (_reservationGate)
        {
            _store.Mutate(d =>
            {
                var index = d.Operations.FindIndex(item => SameScope(item, context));
                if (index >= 0)
                {
                    d.Operations[index] = d.Operations[index] with { IntentFingerprint = fingerprint };
                }
            });
        }

        flight.BindDigest(digest);
        return Task.CompletedTask;
    }

    /// <summary>Binds the safely parsed manifest identity to the reservation, without ever re-keying it to a plugin scope.</summary>
    private void BindIdentity(PluginLifecycleRequestContext context, InFlight? flight, string pluginId, string pluginVersion)
    {
        if (flight is null)
        {
            return;
        }

        lock (_reservationGate)
        {
            _store.Mutate(d =>
            {
                var index = d.Operations.FindIndex(item => SameScope(item, context));
                if (index >= 0)
                {
                    d.Operations[index] = d.Operations[index] with { PluginId = pluginId, PluginVersion = pluginVersion };
                }
            });
        }
    }

    private void Complete(PluginLifecycleRequestContext context, InFlight flight, PluginLifecycleResult result)
    {
        if (flight.Digest is null && flight.Known.Operation == "install" || result.Category == PluginLifecycleResultCategory.InternalFailure)
        {
            Release(context, flight);
            return;
        }

        lock (_reservationGate)
        {
            _store.Mutate(d =>
            {
                var index = d.Operations.FindIndex(item => SameScope(item, context));
                if (index >= 0)
                {
                    d.Operations[index] = d.Operations[index] with
                    {
                        Status = PluginIdempotencyStatus.Completed,
                        ResultCategory = result.Category.ToString(),
                        ResultState = result.State?.ToString(),
                        ResultLifecycleVersion = result.LifecycleVersion,
                        PluginId = result.PluginId ?? d.Operations[index].PluginId,
                        PluginVersion = result.PluginVersion ?? d.Operations[index].PluginVersion,
                        ExpiresAtUtc = _manager.Clock.GetUtcNow() + IdempotencyRetention,
                    };
                }
            });
            _inFlight.Remove(flight.Scope);
        }

        flight.BindDigest(flight.Digest);
        flight.Done.TrySetResult(new FlightOutcome(result, false));
    }

    private void Release(PluginLifecycleRequestContext context, InFlight? flight)
    {
        if (flight is null)
        {
            return;
        }

        lock (_reservationGate)
        {
            _store.Mutate(d => d.Operations.RemoveAll(item => SameScope(item, context)));
            _inFlight.Remove(flight.Scope);
        }

        flight.BindDigest(null);
        flight.Done.TrySetResult(new FlightOutcome(null, true));
    }

    private void DropInterruptedIdempotencyReservations()
    {
        // No owner survives a restart; the journal already reconciled any mutation, so the actor may retry.
        lock (_reservationGate)
        {
            if (_inFlight.Count == 0 && _store.Read().Operations.Any(item => item.Status == PluginIdempotencyStatus.InProgress))
            {
                _store.Mutate(d => d.Operations.RemoveAll(item => item.Status == PluginIdempotencyStatus.InProgress));
            }
        }
    }

    private static bool SameScope(PluginIdempotencyOperation item, PluginLifecycleRequestContext context) =>
        string.Equals(item.NodeId, context.Node.Value, StringComparison.Ordinal) &&
        string.Equals(item.ActorKind, context.Actor.Kind, StringComparison.Ordinal) &&
        string.Equals(item.ActorId, context.Actor.Id, StringComparison.Ordinal) &&
        string.Equals(item.Key, context.IdempotencyKey, StringComparison.Ordinal);

    private static bool KnownFieldsMatch(Intent left, Intent right) =>
        string.Equals(left.Operation, right.Operation, StringComparison.Ordinal) &&
        string.Equals(left.PluginId, right.PluginId, StringComparison.Ordinal) &&
        left.Expected == right.Expected &&
        string.Equals(left.Confirmation, right.Confirmation, StringComparison.Ordinal);

    /// <summary>The canonical request intent: operation, request identity, precondition, confirmation, and archive digest.</summary>
    private static string Fingerprint(Intent intent) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n', intent.Operation, intent.PluginId ?? string.Empty, intent.Expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            intent.Confirmation ?? string.Empty, intent.ArchiveDigest ?? string.Empty))));

    private static PluginLifecycleResult ReplayOf(PluginIdempotencyOperation record) => new(
        Enum.Parse<PluginLifecycleResultCategory>(record.ResultCategory ?? nameof(PluginLifecycleResultCategory.InternalFailure)),
        record.PluginId,
        record.PluginVersion,
        record.ResultState is null ? null : Enum.Parse<PluginLifecycleState>(record.ResultState),
        record.ResultLifecycleVersion,
        "idempotency",
        Replayed: true);

    /// <summary>Hashes a follower's own body under the intake bound; <c>null</c> when it exceeds the compressed-size limit.</summary>
    private async Task<string?> DigestBoundedAsync(Stream archive, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await archive.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            total += read;
            if (total > _limits.MaximumCompressedBytes)
            {
                return null;
            }

            hash.AppendData(buffer, 0, read);
        }
    }

    private sealed record Intent(string Operation, string? PluginId, long? Expected, string? Confirmation, string? ArchiveDigest);

    private sealed record FlightOutcome(PluginLifecycleResult? Result, bool Released);

    private sealed class InFlight(string scope, Intent known)
    {
        public string Scope { get; } = scope;
        public Intent Known { get; } = known;
        public string? Digest { get; private set; }
        public TaskCompletionSource DigestBound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<FlightOutcome> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BindDigest(string? digest)
        {
            Digest ??= digest;
            DigestBound.TrySetResult();
        }
    }
}
