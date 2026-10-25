// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world passes to the test, which disposes it.
#pragma warning disable CA2000

using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;

namespace bOps.Cli.Tests;

/// <summary>
/// The benchmark profile for Qwen3.8-Flash-Next-Smart (128K context, 32K generation) read the way the CLI reads it: the environment overlay
/// of the harness is configuration, <c>Agent</c> binds to <see cref="AgentRunnerOptions"/>, <c>ModelProvider</c> to the transport timeout, and
/// the same validation <c>Program.cs</c> runs accepts it. What the profile says is then what a model request carries.
/// </summary>
public sealed class Qwen38ProfileBindingTests
{
    private static string ProfilePath => Path.Combine(FindRepositoryRoot(), "scripts", "windows", "maf-cli-benchmark-profile.qwen38-smart-128k.example.json");

    private static IConfiguration Configuration()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ProfilePath));
        var profile = document.RootElement;
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ModelProvider:Provider"] = profile.GetProperty("provider").GetString(),
            ["ModelProvider:Model"] = profile.GetProperty("model").GetString(),
            ["ModelProvider:BaseUrl"] = profile.GetProperty("baseUrl").GetString(),
            ["ModelProvider:RequestTimeout"] = profile.GetProperty("requestTimeout").GetString(),
            ["ModelProvider:SupportsNativeToolCalling"] = "true",
            // The shipped appsettings.json of the CLI carries the secret reference; the profile overlay does not touch it.
            ["ModelProvider:ApiKeySecret:Provider"] = "environment",
            ["ModelProvider:ApiKeySecret:Name"] = "BOPS_MODEL_API_KEY",
        };
        foreach (var property in profile.GetProperty("agent").EnumerateObject())
        {
            values["Agent:" + property.Name] = property.Value.ToString();
        }

        foreach (var property in profile.GetProperty("extraConfiguration").EnumerateObject())
        {
            values[property.Name] = property.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void TheProfile_BindsToTheOptionsTheCliBuilds_AndPassesTheCliValidation()
    {
        var configuration = Configuration();
        var model = configuration.GetSection("ModelProvider").Get<ChatModelOptions>()!;
        var options = configuration.GetSection("Agent").Get<AgentRunnerOptions>()!;

        options.Validate(new ChatModelOptions(model.Provider, model.BaseUrl, null, model.Model, true) { RequestTimeout = model.RequestTimeout }.EffectiveRequestTimeout);

        Assert.Equal(32768, options.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromMinutes(45), options.ModelCallAttemptTimeout);
        Assert.Equal(TimeSpan.FromMinutes(50), model.RequestTimeout);
        Assert.True(options.ModelCallBudget >= options.ModelCallMaxAttempts * options.ModelCallAttemptTimeout);
        var context = options.Context.Resolve(new ChatModelDescriptor("LlamaCpp", "Qwen3.8-Flash-Next-Smart"));
        Assert.NotNull(context);
        Assert.Equal((131072, 32768, 2048), (context.MaxContextTokens, context.ReservedOutputTokens, options.Context.SafetyReserveTokens));
        Assert.Null(options.Context.Resolve(new ChatModelDescriptor("LlamaCpp", "Qwen3.6-35B"))); // another model keeps an unknown capacity
    }

    [Fact]
    public async Task EveryModelRequestCarriesTheConfigured32KLimit_AndTheContextAuditNamesTheWindow()
    {
        var options = Configuration().GetSection("Agent").Get<AgentRunnerOptions>()!;
        using var world = new CliKnowledgeWorld
        {
            Options = options with
            {
                Context = options.Context with
                {
                    // The scripted model reports the descriptor fake/fake-model; the profile is the Qwen one, so state the same numbers for it.
                    Profiles = [.. options.Context.Profiles.Select(profile => profile with { Provider = "fake", Model = "fake-model" })],
                },
            },
        };
        var rig = world.Runner([CliScript.Plan("system.events"), CliScript.Tool("c1", "system.events"), CliScript.Final("diagnosis")], settings: null);

        var state = await rig.Runner.RunAsync(CliKnowledgeWorld.Goal, CliKnowledgeWorld.Operator);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.All(rig.Model.Requests, request => Assert.Equal(32768, request.MaxOutputTokens));
        Assert.All(world.Events.OfType<ModelCallAuditEvent>(), call =>
        {
            Assert.Equal(96256, call.Context!.InputBudgetTokens);
            Assert.Equal(32768, call.Output!.RequestedMaxOutputTokens);
        });
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
