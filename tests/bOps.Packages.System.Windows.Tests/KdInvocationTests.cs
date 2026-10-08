// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>ADR-0048 §6: one fixed, allowlisted command sequence; no shell; the dump path is always one separate argument.</summary>
public sealed class KdInvocationTests
{
    private const string Nonce = KernelDumpFixtures.Nonce;

    /// <summary>The complete, reviewed list of debugger commands. Adding one must change this test.</summary>
    private static readonly string[] AllowedCommands = [".bugcheck", "!analyze -v", "lm", "!blackboxpnp", "!blackboxbsd"];

    [Fact]
    public void Script_IsExactlyTheAllowlistedCommandsBetweenMarkers_EndingWithQuit()
    {
        var commands = KdCommandScript.Build(Nonce).Split(';');

        Assert.Equal("q", commands[^1]);
        var debuggerCommands = commands[..^1].Where(command => !command.StartsWith(".echo <<<BOPS_" + Nonce + "_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(AllowedCommands, debuggerCommands);
        Assert.Equal(AllowedCommands.Length * 3 + 1, commands.Length);
    }

    [Fact]
    public void Script_ContainsNoMemoryDisplaySearchExtensionLoadScriptOrShellCommand()
    {
        var commands = KdCommandScript.Build(Nonce).Split(';').Select(command => command.Trim()).ToArray();
        var forbiddenPrefixes = new[]
        {
            "d", "e", "s ", "s-", ".load", ".loadby", ".extpath", ".scriptload", ".scriptrun", ".shell", ".create", ".attach",
            ".writemem", ".dump", ".logopen", "$<", "$><", "$$<", "$$><", "$$>a<", "!load", ".sympath", ".symfix", "!dx", "dx",
        };

        foreach (var command in commands.Where(command => !command.StartsWith(".echo ", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(forbiddenPrefixes, prefix => command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && command != ".bugcheck");
        }
    }

    [Fact]
    public void Script_RejectsANonceThatCouldCarrySyntax()
    {
        Assert.Throws<ArgumentException>(() => KdCommandScript.Build("0123456789ABCDE;"));
        Assert.Throws<ArgumentException>(() => KdCommandScript.Build("short"));
        Assert.Matches("^[0-9A-F]{16}$", KdCommandScript.NewNonce());
        Assert.NotEqual(KdCommandScript.NewNonce(), KdCommandScript.NewNonce());
    }

    [Fact]
    public void Arguments_KeepTheDumpPathAsOneSeparateElement_OutsideTheCommandString()
    {
        const string dump = @"C:\Windows\Minidump\name with spaces (1).dmp";
        var arguments = KdCommandScript.Arguments(dump, "srv*C:\\cache*https://msdl.microsoft.com/download/symbols", Nonce);

        Assert.Equal(["-noshell", "-sins", "-y", "srv*C:\\cache*https://msdl.microsoft.com/download/symbols", "-z", dump, "-c", KdCommandScript.Build(Nonce)], arguments);
        Assert.DoesNotContain(dump, arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void StartInfo_NeverUsesAShell_AndPassesEveryArgumentThroughArgumentList()
    {
        const string hostile = "C:\\Windows\\Minidump\\a\" & calc.exe & \".dmp";
        var startInfo = DebuggerProcessRunner.CreateStartInfo(@"C:\Debuggers\kd.exe", ["-z", hostile], @"C:\work");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardInput && startInfo.RedirectStandardOutput && startInfo.RedirectStandardError);
        Assert.Equal(@"C:\Debuggers\kd.exe", startInfo.FileName);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(["-z", hostile], startInfo.ArgumentList);
        Assert.Equal(@"C:\work", startInfo.WorkingDirectory);
    }

    [Fact]
    public void StartInfo_StripsEnvironmentThatRedirectsSymbolsExtensionsOrScripts()
    {
        Assert.True(DebuggerProcessRunner.IsStrippedEnvironmentVariable("_NT_SYMBOL_PATH"));
        Assert.True(DebuggerProcessRunner.IsStrippedEnvironmentVariable("_nt_debugger_extension_path"));
        Assert.True(DebuggerProcessRunner.IsStrippedEnvironmentVariable("_NT_ALT_SYMBOL_PATH"));
        Assert.True(DebuggerProcessRunner.IsStrippedEnvironmentVariable("DBGHELP_LOG"));
        Assert.True(DebuggerProcessRunner.IsStrippedEnvironmentVariable("INIT"));
        Assert.False(DebuggerProcessRunner.IsStrippedEnvironmentVariable("PATH"));

        Environment.SetEnvironmentVariable("_NT_DEBUGGER_EXTENSION_PATH", @"C:\evil");
        try
        {
            var startInfo = DebuggerProcessRunner.CreateStartInfo(@"C:\Debuggers\kd.exe", [], @"C:\work");
            Assert.DoesNotContain(startInfo.Environment.Keys, DebuggerProcessRunner.IsStrippedEnvironmentVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_DEBUGGER_EXTENSION_PATH", null);
        }
    }

    [Fact]
    public async Task Runner_RefusesAShellStartInfo()
    {
        var startInfo = new ProcessStartInfo("kd.exe") { UseShellExecute = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => DebuggerProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(1), 100, CancellationToken.None));
    }

    [Fact]
    public async Task Runner_ReportsAnExecutableThatCannotStart()
    {
        var startInfo = DebuggerProcessRunner.CreateStartInfo(Path.Combine(AppContext.BaseDirectory, "no-such-debugger.exe"), [], AppContext.BaseDirectory);

        var result = await DebuggerProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(5), 100, CancellationToken.None);

        Assert.False(result.Started);
    }

    // The children below are test-only stand-ins for kd.exe (as WindowsUpdatesTests does): the runner must behave the same for
    // any executable, and Debugging Tools are not installed on CI.

    [WindowsOnlyFact]
    public async Task Runner_CapturesStdoutStderrAndExitCode_AndBoundsStdout()
    {
        var startInfo = DebuggerProcessRunner.CreateStartInfo(Cmd, ["/d", "/c", "echo 0123456789ABCDEFGHIJ& echo problem 1>&2& exit /b 3"], AppContext.BaseDirectory);

        var result = await DebuggerProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(30), 10, CancellationToken.None);

        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("0123456789", result.StandardOutput);
        Assert.True(result.StandardOutputTruncated);
        Assert.Contains("problem", result.StandardError, StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public async Task Runner_KillsTheProcessTreeAtItsBound_AndReportsTimedOut()
    {
        var startInfo = DebuggerProcessRunner.CreateStartInfo(Cmd, ["/d", "/c", "echo started& ping 127.0.0.1 -n 30 > nul"], AppContext.BaseDirectory);
        var watch = Stopwatch.StartNew();

        var result = await DebuggerProcessRunner.RunAsync(startInfo, TimeSpan.FromMilliseconds(500), 1_000, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.Contains("started", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "the tree was not killed at the bound");
    }

    [WindowsOnlyFact]
    public async Task Runner_PropagatesCallerCancellation_AfterKillingTheTree()
    {
        var startInfo = DebuggerProcessRunner.CreateStartInfo(Cmd, ["/d", "/c", "ping 127.0.0.1 -n 30 > nul"], AppContext.BaseDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DebuggerProcessRunner.RunAsync(startInfo, TimeSpan.FromMinutes(5), 1_000, cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "cancellation did not stop the child");
    }

    [WindowsOnlyFact]
    public async Task Runner_DoesNotWaitForInput()
    {
        var startInfo = DebuggerProcessRunner.CreateStartInfo(Cmd, ["/d", "/c", "set /p answer=& echo done"], AppContext.BaseDirectory);

        var result = await DebuggerProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(30), 1_000, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Contains("done", result.StandardOutput, StringComparison.Ordinal);
    }

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");
}
