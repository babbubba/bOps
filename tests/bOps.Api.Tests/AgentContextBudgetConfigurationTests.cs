// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Runtime;
using Microsoft.Extensions.Configuration;

namespace bOps.Api.Tests;

/// <summary>Shipped defaults, binding and fail-fast validation for context and active-attempt budgets (HARDEN-8, ADR-0052).</summary>
public sealed class AgentContextBudgetConfigurationTests
{
    private static AgentRunnerOptions Bind(IConfiguration configuration) =>
        configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();

    private static IConfigurationRoot Shipped() =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build();

    [Fact]
    public void ShippedSettings_StateAcceptedDefaults()
    {
        var settings = Shipped();
        var options = Bind(settings);

        Assert.Equal("450000", settings["Agent:MaxTotalTokens"]);
        Assert.Equal("01:00:00", settings["Agent:MaxAttemptDuration"]);
        Assert.Equal(450_000, options.MaxTotalTokens);
        Assert.Equal(TimeSpan.FromHours(1), options.MaxAttemptDuration);
        options.Validate();
    }

    [Fact]
    public void ShippedSettings_ConfigureNoGuessedContextProfileAndKeepSummarizationOff()
    {
        var options = Bind(Shipped());

        // ADR-0052: capacity is operator knowledge. The shipped default router has no profile entry, so its capacity stays unknown;
        // summarization is an opt-in with a possible data-egress destination.
        Assert.Empty(options.Context.Profiles);
        Assert.False(options.Summarization.Enabled);
        Assert.False(options.Summarization.IsDedicated);
    }

    [Fact]
    public void ARemovedHistoryOptionInAnExistingConfiguration_IsIgnoredNotRejected()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:VerbatimHistorySteps"] = "3" })
            .Build();

        Bind(configuration).Validate();
    }

    [Theory]
    [InlineData("Agent:MaxTotalTokens", "0")]
    [InlineData("Agent:MaxAttemptDuration", "00:00:00")]
    [InlineData("Agent:MaxAttemptDuration", "1.00:00:01")]
    [InlineData("Agent:Context:SafetyReserveTokens", "-1")]
    [InlineData("Agent:Summarization:TriggerRatio", "0")]
    [InlineData("Agent:Summarization:RecentGroupsToPreserve", "65")]
    public void InvalidOverrides_FailValidationWithoutClamping(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.Throws<InvalidOperationException>(() => Bind(configuration).Validate());
    }

    [Fact]
    public void ExplicitNullsDisableOptionalBudgets()
    {
        var options = new AgentRunnerOptions { MaxTotalTokens = null, MaxAttemptDuration = null };

        options.Validate();

        Assert.Null(options.MaxTotalTokens);
        Assert.Null(options.MaxAttemptDuration);
    }
}
