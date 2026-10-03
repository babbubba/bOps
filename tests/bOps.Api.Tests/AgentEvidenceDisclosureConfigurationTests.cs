// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Runtime;
using Microsoft.Extensions.Configuration;

namespace bOps.Api.Tests;

/// <summary>
/// HARDEN-9 / ADR-0042 §6: <c>Agent:EvidenceDisclosureRetries</c> binds from configuration like the other budget options, the shipped
/// settings state its default, and any value other than 0 and 1 fails options validation — which both hosts run at start-up, when
/// they build the runner — instead of enabling an unbounded or undefined number of re-asks.
/// </summary>
public sealed class AgentEvidenceDisclosureConfigurationTests
{
    private static AgentRunnerOptions Bind(IConfiguration configuration) =>
        configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();

    private static IConfigurationRoot Shipped() =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build();

    private static IConfigurationRoot Overridden(string value) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:EvidenceDisclosureRetries"] = value })
            .Build();

    [Fact]
    public void TheShippedSettings_StateTheDefault_AndKeepTheObservationBudgetUnchanged()
    {
        var options = Bind(Shipped());

        Assert.Equal("1", Shipped()["Agent:EvidenceDisclosureRetries"]);
        Assert.Equal(1, options.EvidenceDisclosureRetries);
        Assert.Equal(4000, options.MaxObservationCharacters);
        options.Validate();
    }

    [Fact]
    public void WithoutTheKey_TheDefaultIsOne()
    {
        var options = Bind(new ConfigurationBuilder().Build());

        Assert.Equal(1, options.EvidenceDisclosureRetries);
        options.Validate();
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    public void ZeroAndOne_BindAndValidate(string value, int expected)
    {
        var options = Bind(Overridden(value));

        Assert.Equal(expected, options.EvidenceDisclosureRetries);
        options.Validate();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("10")]
    public void AnyOtherValue_FailsOptionsValidation_NamingTheKey(string value)
    {
        var options = Bind(Overridden(value));

        var failure = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("'Agent:EvidenceDisclosureRetries'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartUpValidationOfTheApi_RejectsAnInvalidValue_EvenWithAProviderTimeout()
    {
        // The API validates with the provider transport timeout (ADR-0039); the runner validates again in its constructor.
        var options = Bind(Overridden("2"));

        Assert.Throws<InvalidOperationException>(() => options.Validate(providerRequestTimeout: TimeSpan.FromMinutes(5)));
    }
}
