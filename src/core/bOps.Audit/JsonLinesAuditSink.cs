using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Audit;

/// <summary>
/// Writes audit events as newline-delimited JSON, one line per event, append-only. The initial
/// V0.1 implementation — a SQLite-backed sink can replace it later without changing
/// <see cref="IAuditSink"/> (plan §9). Hash-chaining for tamper evidence is a V0.3 addition
/// (agentic/06-decisions.md, D-008); until then this file is append-only by convention, not
/// tamper-evident, and must be described that way.
/// </summary>
public sealed class JsonLinesAuditSink : IAuditSink, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonLinesAuditSink(string filePath)
    {
        _filePath = filePath;
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(evt, AuditJsonContext.Default.AuditEvent);

        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
