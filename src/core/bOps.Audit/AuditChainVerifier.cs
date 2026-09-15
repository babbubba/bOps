using System.Text.Json;

namespace bOps.Audit;

/// <summary>The result of checking a <see cref="JsonLinesAuditSink"/> file's hash chain (V0.3, ADR-0015).</summary>
public sealed record AuditChainVerificationResult(bool IsValid, long? BrokenAtSequence, string? Reason)
{
    /// <summary>A result for a chain with no breaks — including an empty file, which is trivially valid.</summary>
    public static AuditChainVerificationResult Valid { get; } = new(true, null, null);
}

/// <summary>
/// Independently re-derives every hash in a <see cref="JsonLinesAuditSink"/> file and confirms
/// it matches what is stored, and that each line's <c>prevHash</c> matches the previous line's
/// <c>hash</c> — the check that makes the chain in <see cref="JsonLinesAuditSink"/> worth
/// anything (agentic/03-security-rules.md, rule S9; agentic/06-decisions.md, D-008). This does
/// not run automatically anywhere yet; it exists so the chain is actually checkable, not merely
/// present. A CLI command to run it on demand is a reasonable, small follow-up, not done here —
/// see the V0.3 ADR.
/// </summary>
public static class AuditChainVerifier
{
    /// <summary>Verifies every line of an in-memory sequence, most useful for tests.</summary>
    public static AuditChainVerificationResult Verify(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var expectedPrevHash = JsonLinesAuditSink.GenesisHash;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AuditChainEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditChainEnvelope);
            }
            catch (JsonException ex)
            {
                return new AuditChainVerificationResult(false, null, $"Line is not valid JSON: {ex.Message}");
            }

            if (envelope is null)
            {
                return new AuditChainVerificationResult(false, null, "Line deserialized to null.");
            }

            if (!string.Equals(envelope.PrevHash, expectedPrevHash, StringComparison.Ordinal))
            {
                return new AuditChainVerificationResult(false, envelope.Seq,
                    $"prevHash does not match the previous line's hash — the chain was broken (reordered, edited, or a line removed).");
            }

            var expectedHash = JsonLinesAuditSink.ComputeHash(envelope.PrevHash, envelope.EventJson);
            if (!string.Equals(envelope.Hash, expectedHash, StringComparison.Ordinal))
            {
                return new AuditChainVerificationResult(false, envelope.Seq,
                    "hash does not match the recomputed value — this line's event content was altered after it was written.");
            }

            expectedPrevHash = envelope.Hash;
        }

        return AuditChainVerificationResult.Valid;
    }

    /// <summary>Verifies the on-disk file at <paramref name="filePath"/>. Returns <see cref="AuditChainVerificationResult.Valid"/> for a file that does not exist — nothing has been written, so nothing can have been tampered with.</summary>
    public static AuditChainVerificationResult VerifyFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        return File.Exists(filePath) ? Verify(File.ReadLines(filePath)) : AuditChainVerificationResult.Valid;
    }
}
