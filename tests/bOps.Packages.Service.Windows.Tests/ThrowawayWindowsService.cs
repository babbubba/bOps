// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace bOps.Packages.Service.Windows.Tests;

/// <summary>
/// Installs a uniquely-named, real instance of <c>bOpsTestService.exe</c>
/// (<c>bOps.TestFixtures.WindowsService</c>) as an actual Windows Service via <c>sc.exe create</c>
/// on construction, and removes it via <c>sc.exe delete</c> on <see cref="Dispose"/> — every test
/// gets its own throwaway service, never a shared one, so there is no cross-test ordering
/// dependency on its current running state.
/// </summary>
internal sealed class ThrowawayWindowsService : IDisposable
{
    public string Name { get; } = $"bops-test-{Guid.NewGuid():N}";

    public ThrowawayWindowsService()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "bOpsTestService.exe");
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("The bOpsTestService.exe test fixture was not found next to the test assembly.", exePath);
        }

        RunSc("create", Name, "binPath=", $"\"{exePath}\"", "start=", "demand");
    }

    public void Dispose()
    {
        // Best-effort: the service may already be stopped, or never successfully started.
        try
        {
            RunSc("stop", Name);
        }
        catch (InvalidOperationException)
        {
            // Already stopped, or never started — fine, delete still proceeds.
        }

        RunSc("delete", Name);
    }

    private static void RunSc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start sc.exe.");

        // Both streams must be drained concurrently with waiting for exit — reading one to
        // completion before starting the other risks a classic pipe deadlock if the child fills
        // the other stream's buffer first.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe {string.Join(' ', arguments)} exited with code {process.ExitCode}: {stdout}{stderr}");
        }
    }
}
