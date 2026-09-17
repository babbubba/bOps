// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Web.Tests;

/// <summary>
/// Opt-in live smoke test against a real, operator-provided SearXNG instance — "does this
/// instance still answer the JSON contract we assume", the same question
/// agentic/04-testing-rules.md's <c>LiveModel</c> category answers for LLM providers. Reuses that
/// exact category (rather than a new one) so it is excluded by the same
/// <c>--filter "Category!=LiveModel"</c> every CI workflow and the local validation command
/// already use, without a second exclusion list to keep in sync. No-ops when
/// <c>SEARXNG_LIVE_BASE_URL</c> is not set — xUnit v2 has no built-in runtime skip.
/// </summary>
[Trait("Category", "LiveModel")]
public sealed class SearxngClientLiveTests
{
    [Fact]
    public async Task SearchAsync_ReturnsResults_AgainstARealConfiguredInstance()
    {
        var baseUrl = Environment.GetEnvironmentVariable("SEARXNG_LIVE_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        var options = new WebSearchOptions { BaseUrl = baseUrl };
        using var client = new SearxngClient(options);

        var outcome = await client.SearchAsync(new SearxngQuery("bOps agent runtime", null, null, null, null, null), CancellationToken.None);

        Assert.True(outcome.Success, outcome.FailureReason);
    }
}
