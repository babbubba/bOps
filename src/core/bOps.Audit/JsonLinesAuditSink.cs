using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Audit;

/// <summary>
/// Writes audit events as newline-delimited JSON, one line per event, append-only, hash-chained
/// for tamper evidence (V0.3, ADR-0015; agentic/06-decisions.md, D-008). Each line is an
/// <see cref="AuditChainEnvelope"/>: the raw event, plus a hash of the previous line's hash and
/// this event's own JSON — so altering, removing, or reordering any historical line breaks the
/// chain from that point forward, detectable with <see cref="AuditChainVerifier"/>. This is
/// tamper-*evident*, not tamper-*proof*: nothing stops someone with write access to the file from
/// rewriting it from a given point and recomputing every hash after it — the chain proves nothing
/// was altered without also being recomputed, not that the file's custodian is honest. The
/// initial V0.1 implementation had neither the chain nor that caveat; a SQLite-backed sink can
/// still replace this one later without changing <see cref="IAuditSink"/> (plan §9).
/// </summary>
public sealed class JsonLinesAuditSink : IAuditSink, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string _lastHash;
    private long _nextSequence;

    /// <summary>The <see cref="AuditChainEnvelope.PrevHash"/> of the first event ever written to a given file — there is no real predecessor to hash.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Opens (or creates) an audit sink backed by the newline-delimited file at
    /// <paramref name="filePath"/>, continuing its hash chain if it already has entries.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file already exists but its last line is not a valid hash-chained entry — most likely
    /// because it predates hash-chaining (a pre-V0.3 file of raw events) or its last write was
    /// interrupted mid-line. Refuses to continue a chain from a line it cannot trust, rather than
    /// silently starting one with a meaningless <c>PrevHash</c>.
    /// </exception>
    public JsonLinesAuditSink(string filePath)
    {
        _filePath = filePath;
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        (_lastHash, _nextSequence) = ReadChainTail(filePath);
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var eventJson = JsonSerializer.Serialize(evt, AuditJsonContext.Default.AuditEvent);

        await _writeLock.WaitAsync(ct);
        try
        {
            var hash = ComputeHash(_lastHash, eventJson);
            var envelope = new AuditChainEnvelope(_nextSequence, _lastHash, hash, eventJson);
            var line = JsonSerializer.Serialize(envelope, AuditJsonContext.Default.AuditChainEnvelope);

            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, ct);

            _lastHash = hash;
            _nextSequence++;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Computes the hash for one chain link: <c>SHA256(previousHash + eventJson)</c>, lowercase hex.</summary>
    internal static string ComputeHash(string previousHash, string eventJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + eventJson)));

    private static (string LastHash, long NextSequence) ReadChainTail(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return (GenesisHash, 0);
        }

        string? lastLine = null;
        foreach (var line in File.ReadLines(filePath))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lastLine = line;
            }
        }

        if (lastLine is null)
        {
            return (GenesisHash, 0);
        }

        var envelope = JsonSerializer.Deserialize(lastLine, AuditJsonContext.Default.AuditChainEnvelope)
            ?? throw new InvalidOperationException($"'{filePath}' has content that is not a valid audit chain envelope.");

        // A JSON object missing "Hash"/"PrevHash" still deserializes successfully — the
        // constructor parameters just take their default (null for a string) — so this is not
        // redundant with the null-check above. Trusting that silently would continue the chain
        // from a null PrevHash, corrupting the very invariant this sink exists to provide. This
        // is also what an old pre-V0.3 file (raw events, no envelope) or a truncated last line
        // looks like, so it doubles as the guard against both.
        if (envelope.Hash is null || envelope.PrevHash is null)
        {
            throw new InvalidOperationException(
                $"'{filePath}' does not end with a valid hash-chained entry — it may predate hash-chaining " +
                "(V0.3, ADR-0015) or its last line may be truncated or corrupted. Move it aside (it is still " +
                "readable as plain history) and let a new file start a fresh chain, or repair it by hand.");
        }

        return (envelope.Hash, envelope.Seq + 1);
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();
}

/// <summary>
/// One hash-chained line in a <see cref="JsonLinesAuditSink"/> file. Internal: this is a storage
/// detail of this specific sink, not part of the <see cref="IAuditSink"/> contract — a different
/// sink implementation is free to represent tamper evidence differently.
/// </summary>
/// <param name="EventJson">
/// The event, as the exact JSON text that was hashed — stored as a JSON string value (not a
/// nested object) deliberately: a string round-trips through JSON encoding byte-for-byte, while
/// re-serializing a reparsed <c>JsonObject</c> is not guaranteed to reproduce the identical text
/// that was actually hashed, which would make <see cref="AuditChainVerifier"/> report tampering
/// that never happened.
/// </param>
internal sealed record AuditChainEnvelope(long Seq, string PrevHash, string Hash, string EventJson);
