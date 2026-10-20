// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Runtime;
using Microsoft.Extensions.Configuration;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0042 §6 (AF-05H amendment): <c>Agent:EvidenceDisclosureEnabled</c> switches the deterministic disclosure section, which
/// makes no model call. The former <c>Agent:EvidenceDisclosureRetries</c> is deprecated but still read so an existing
/// configuration keeps its meaning: <c>0</c> disables the disclosure, <c>1</c> changes nothing, any other value fails options
/// validation — which both hosts run at start-up.
/// </summary>
public sealed class AgentEvidenceDisclosureConfigurationTests
{
    private static AgentRunnerOptions Bind(IConfiguration configuration) =>
        configuration.GetSection("Agent").Get<AgentRunnerOptions>() ?? new AgentRunnerOptions();

    private static IConfigurationRoot Shipped() =>
        new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build();

    private static IConfigurationRoot Overridden(string key, string value) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

    [Fact]
    public void TheShippedSettings_EnableTheDeterministicDisclosure_AndNoLongerShipTheDeprecatedKey()
    {
        var options = Bind(Shipped());

        Assert.Equal("true", Shipped()["Agent:EvidenceDisclosureEnabled"], ignoreCase: true);
        Assert.Null(Shipped()["Agent:EvidenceDisclosureRetries"]);
        Assert.True(options.EvidenceDisclosureEnabled);
        Assert.Null(options.EvidenceDisclosureRetries);
        Assert.True(options.EvidenceDisclosureActive);
        Assert.Equal(4000, options.MaxObservationCharacters);
        options.Validate();
    }

    [Fact]
    public void WithoutAnyKey_TheDisclosureIsOn()
    {
        var options = Bind(new ConfigurationBuilder().Build());

        Assert.True(options.EvidenceDisclosureActive);
        options.Validate();
    }

    [Theory]
    [InlineData("Agent:EvidenceDisclosureEnabled", "false", false)]
    [InlineData("Agent:EvidenceDisclosureEnabled", "true", true)]
    [InlineData("Agent:EvidenceDisclosureRetries", "0", false)]
    [InlineData("Agent:EvidenceDisclosureRetries", "1", true)]
    public void TheSwitchAndTheDeprecatedAlias_BindAndValidate(string key, string value, bool active)
    {
        var options = Bind(Overridden(key, value));

        Assert.Equal(active, options.EvidenceDisclosureActive);
        options.Validate();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("10")]
    public void AnyOtherDeprecatedValue_FailsOptionsValidation_NamingBothKeys(string value)
    {
        var options = Bind(Overridden("Agent:EvidenceDisclosureRetries", value));

        var failure = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("'Agent:EvidenceDisclosureRetries'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'Agent:EvidenceDisclosureEnabled'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartUpValidationOfTheApi_RejectsAnInvalidValue_EvenWithAProviderTimeout()
    {
        // The API validates with the provider transport timeout (ADR-0039); the runner validates again in its constructor.
        var options = Bind(Overridden("Agent:EvidenceDisclosureRetries", "2"));

        Assert.Throws<InvalidOperationException>(() => options.Validate(providerRequestTimeout: TimeSpan.FromMinutes(5)));
    }
}
