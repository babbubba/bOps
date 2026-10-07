// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Runtime;
using Microsoft.Extensions.Configuration;

namespace bOps.Api.Tests;

/// <summary>HARDEN-8 shipped defaults, binding and fail-fast validation for context and active-attempt budgets.</summary>
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

        Assert.Equal("3", settings["Agent:VerbatimHistorySteps"]);
        Assert.Equal("450000", settings["Agent:MaxTotalTokens"]);
        Assert.Equal("01:00:00", settings["Agent:MaxAttemptDuration"]);
        Assert.Equal(3, options.VerbatimHistorySteps);
        Assert.Equal(450_000, options.MaxTotalTokens);
        Assert.Equal(TimeSpan.FromHours(1), options.MaxAttemptDuration);
        options.Validate();
    }

    [Theory]
    [InlineData("Agent:VerbatimHistorySteps", "-1")]
    [InlineData("Agent:VerbatimHistorySteps", "16")]
    [InlineData("Agent:MaxTotalTokens", "0")]
    [InlineData("Agent:MaxAttemptDuration", "00:00:00")]
    [InlineData("Agent:MaxAttemptDuration", "1.00:00:01")]
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

