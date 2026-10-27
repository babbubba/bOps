// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace bOps.Cli.Tests;

/// <summary>
/// BOPS-0122 (C01-R22): the process-level contract of the opt-in MAF governed turn host, which only the real <c>bops</c> executable
/// decides: a malformed value is refused with the configuration exit code before anything is composed, a value that cannot apply to
/// the command is ignored with a message and changes no exit code, and the experiment is announced exactly when it is on and used.
/// Offline: no provider is ever reached (the model address is a closed port, and every command here ends before its first model call).
/// </summary>
public sealed class MafCliProcessTests : IDisposable
{
    private const string Key = "Cli__Experimental__MafGovernedTurn";
    private const string Banner = "EXPERIMENTAL: each model turn is hosted by a Microsoft Agent Framework agent";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bops-cli-maf-{Guid.NewGuid():N}");

    public MafCliProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed is not a test failure.
        }
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("yes")]
    public async Task AMalformedValue_IsRefusedWithExitCodeTwo_AndNamesTheKey(string value)
    {
        var (code, _, error) = await RunAsync(value, "resume", Guid.NewGuid().ToString());

        Assert.Equal(2, code);
        Assert.Contains("Cli:Experimental:MafGovernedTurn", error, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", error, StringComparison.Ordinal);
        Assert.DoesNotContain(Banner, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    public async Task WhenOffOrUnset_ARunIsUnchanged_AndNothingIsAnnounced(string? value)
    {
        var (code, _, error) = await RunAsync(value, "resume", Guid.NewGuid().ToString());

        Assert.Equal(1, code);
        Assert.Contains("No stored task with id", error, StringComparison.Ordinal);
        Assert.DoesNotContain("EXPERIMENTAL", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOn_AResumeEndsWithTheSameExitCodeAsWhenOff_AndTheMessageOfThatRefusal()
    {
        var off = await RunAsync("false", "resume", Guid.NewGuid().ToString());
        var on = await RunAsync("true", "resume", Guid.NewGuid().ToString());

        // A refusal that happens before the first model turn is the same on both paths: the host changes how a turn is hosted, not what is refused.
        Assert.Equal(off.ExitCode, on.ExitCode);
        Assert.Contains("No stored task with id", on.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandThatIsNotAGoalOrAResume_IgnoresTheValue_AndKeepsItsExitCode()
    {
        var off = await RunAsync("false", "delegate");
        var on = await RunAsync("true", "delegate");

        Assert.Equal(1, off.ExitCode);
        Assert.Equal(off.ExitCode, on.ExitCode);
        Assert.Contains("Usage: bops delegate", on.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Banner, on.Error, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string? mafValue, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bops.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var inherited in start.Environment.Keys.Where(key =>
                     key.StartsWith("ModelProvider__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Cli__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(inherited);
        }

        start.Environment["ModelProvider__Provider"] = "Ollama";
        start.Environment["ModelProvider__BaseUrl"] = "http://127.0.0.1:9";
        start.Environment["ModelProvider__Model"] = "none";
        start.Environment["ModelProvider__SupportsNativeToolCalling"] = "false";
        start.Environment["ModelProvider__ApiKeySecret__Provider"] = "environment";
        start.Environment["ModelProvider__ApiKeySecret__Name"] = "BOPS_TEST_KEY";
        start.Environment["BOPS_TEST_KEY"] = "not-a-real-key";
        start.Environment["OTEL_SDK_DISABLED"] = "true";
        if (mafValue is not null)
        {
            start.Environment[Key] = mafValue;
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }
}
