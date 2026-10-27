// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace bOps.Cli.Tests;

/// <summary>
/// C01-R23: what only the real <c>bops</c> executable decides for <c>bops diagnose</c>. The workflow is disabled unless the host enables
/// it, a malformed value is refused with the configuration exit code before anything is composed, a bad invocation is a usage error, and
/// the ordinary commands are not changed by the new one. Offline: no provider is ever reached.
/// </summary>
public sealed class MafDiagnoseCliProcessTests : IDisposable
{
    private const string Key = "Cli__Experimental__MafWorkflow";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bops-cli-diagnose-{Guid.NewGuid():N}");

    public MafDiagnoseCliProcessTests() => Directory.CreateDirectory(_dir);

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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    public async Task WhenNotEnabled_TheWorkflowDoesNotRun_AndTheMessageSaysHowToOptIn(string? value)
    {
        var (code, output, error) = await RunAsync(value, "diagnose", "Windows restarts", "--workflow", "maf");

        Assert.Equal(2, code);
        Assert.Contains("disabled", error, StringComparison.Ordinal);
        Assert.Contains("Cli:Experimental:MafWorkflow", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Workflow started", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedValue_IsRefusedWithTheConfigurationExitCode()
    {
        var (code, _, error) = await RunAsync("maybe", "diagnose", "Windows restarts", "--workflow", "maf");

        Assert.Equal(2, code);
        Assert.Contains("'maybe'", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("diagnose", "Windows restarts")]
    [InlineData("diagnose", "Windows restarts", "--workflow", "other")]
    [InlineData("diagnose", "--workflow", "maf")]
    public async Task ABadInvocation_IsAUsageError_BeforeAnythingIsComposed(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var (code, _, error) = await RunAsync("true", args);

        Assert.Equal(1, code);
        Assert.Contains("Usage: bops diagnose", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOrdinaryCommands_AreNotChangedByTheWorkflowSwitch()
    {
        var off = await RunAsync("false", "resume", Guid.NewGuid().ToString());
        var on = await RunAsync("true", "resume", Guid.NewGuid().ToString());

        Assert.Equal(1, off.ExitCode);
        Assert.Equal(off.ExitCode, on.ExitCode);
        Assert.Contains("No stored task with id", on.Error, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string? value, params string[] args)
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
        if (value is not null)
        {
            start.Environment[Key] = value;
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
