using System.Diagnostics;
using System.Text;

namespace bOps.Packages.Scheduler.Linux;

internal sealed record LinuxSystemdTimerSnapshot(string Id, string Description, string? UnitFileState, string? Unit, string? Result, long? NextRealtime, long? NextMonotonic, long? LastTrigger, string? Calendar, string? Monotonic, bool Complete);

internal sealed record LinuxProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface ILinuxProcessRunner
{
    Task<LinuxProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct);
}

internal sealed class LinuxProcessRunner : ILinuxProcessRunner
{
    public async Task<LinuxProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (executable is "systemctl") { start.Environment["LC_ALL"] = "C"; start.Environment["LANG"] = "C"; }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
