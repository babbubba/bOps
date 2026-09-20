// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Cli;

namespace bOps.Cli.Tests;

/// <summary>The prompt a human answers before a delegated plan is applied: what it shows, and what counts as a yes.</summary>
public sealed class ConsolePlanApprovalProviderTests
{
    private static readonly ActorIdentity Operator = new("os-user", "alice", "Alice");
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static PlanApprovalRequest Request(AuthorityEnvelope? authority = null)
    {
        var plan = new ExecutionPlan(
            "service.restore", "1.0.0", "Restart the service that stopped.",
            [new ExecutionPlanStep(0, "service.restart", ToolArguments.FromJson(new JsonObject { ["name"] = "nginx" }), "Restart nginx.")]);
        return new PlanApprovalRequest(
            Guid.NewGuid(), ExecutionPlanHasher.ComputeHash(plan), plan, "service.skill", "service.restore", "web-1", "prod", BlastRadius.Single,
            [new Finding("f-1", "nginx has stopped.", ["discovery-0", "discovery-1"], RiskLevel.High)])
        {
            Authority = authority,
        };
    }

    private static AuthorityEnvelope Envelope() => new(
        Operator, Depth: 1, ["service.skill"], ["service.restore"], ["service.restart", "service.status"], RiskLevel.High, BlastRadius.Single,
        ["web-1"], ["prod"], new DelegationBudget(6, 0, Now.AddMinutes(10)), new MaintenanceWindow(Now, Now.AddHours(1)));

    private static async Task<(ApprovalDecision Decision, string Shown)> AskAsync(PlanApprovalRequest request, string typed)
    {
        var output = new StringWriter();
        var provider = new ConsolePlanApprovalProvider(new StringReader(typed), output, Operator);
        var decision = await provider.RequestPlanApprovalAsync(request);
        return (decision, output.ToString());
    }

    [Fact]
    public async Task ShowsTheHashTheStepsTheFindingsAndTheAuthorityTheChangeRunsUnder()
    {
        var request = Request(Envelope());

        var (_, shown) = await AskAsync(request, "n\n");

        Assert.Contains(request.DelegationId.ToString(), shown, StringComparison.Ordinal);
        Assert.Contains(request.PlanHash, shown, StringComparison.Ordinal);
        Assert.Contains("service.skill / service.restore on 'web-1' (prod), blast radius Single", shown, StringComparison.Ordinal);
        Assert.Contains("Restart the service that stopped.", shown, StringComparison.Ordinal);
        Assert.Contains("[0] service.restart {\"name\":\"nginx\"}", shown, StringComparison.Ordinal);
        Assert.Contains("[High] nginx has stopped. (evidence: discovery-0, discovery-1)", shown, StringComparison.Ordinal);
        Assert.Contains("service.restart, service.status", shown, StringComparison.Ordinal);
        Assert.Contains("High risk, Single blast radius", shown, StringComparison.Ordinal);
        Assert.Contains("web-1", shown, StringComparison.Ordinal);
        Assert.Contains("6 steps", shown, StringComparison.Ordinal);
        Assert.Contains("Window:", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaysSoWhenTheAuthorityWasNotStated()
    {
        var (_, shown) = await AskAsync(Request(), "n\n");

        Assert.Contains("(not stated)", shown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("y\n")]
    [InlineData("Y\n")]
    [InlineData("yes\n")]
    [InlineData("  YES  \n")]
    public async Task AnExplicitYesApproves_AsTheOperatorWhoAnsweredAndWithTheirNote(string typed)
    {
        var (decision, shown) = await AskAsync(Request(Envelope()), typed + "looks right\n");

        Assert.True(decision.Approved);
        Assert.Equal(Operator, decision.Actor);
        Assert.Equal("looks right", decision.Note);
        Assert.Contains("Optional note:", shown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("maybe\n")]
    [InlineData("yy\n")]
    [InlineData("")]
    public async Task AnythingButAnExplicitYes_IsANo_IncludingAClosedInput(string typed)
    {
        var (decision, shown) = await AskAsync(Request(Envelope()), typed);

        Assert.False(decision.Approved);
        Assert.Equal(Operator, decision.Actor);
        Assert.Null(decision.Note);
        Assert.DoesNotContain("Optional note:", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AYesWithAnEmptyNote_HasNoNote()
    {
        var (decision, _) = await AskAsync(Request(Envelope()), "y\n\n");

        Assert.True(decision.Approved);
        Assert.Null(decision.Note);
    }
}
