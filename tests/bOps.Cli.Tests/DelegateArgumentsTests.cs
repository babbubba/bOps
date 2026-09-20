// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Cli;

namespace bOps.Cli.Tests;

/// <summary>What <c>bops delegate</c> accepts on its command line, and what it says when it does not.</summary>
public sealed class DelegateArgumentsTests
{
    private static readonly string RunId = Guid.NewGuid().ToString();

    [Fact]
    public void AnObjectiveAloneStartsADiagnosis_WithNoChangeAndNoNarrowing()
    {
        var (invocation, error) = DelegateArguments.Parse(["Find", "out", "why", "nginx", "stopped"]);

        Assert.Null(error);
        Assert.Equal(DelegateAction.Start, invocation!.Action);
        Assert.Equal("Find out why nginx stopped", invocation.Request!.Objective);
        Assert.Null(invocation.Request.Remediation);
        Assert.Null(invocation.Request.Authority?.MaxSteps);
        Assert.Null(invocation.Request.Authority?.MaxTokens);
        Assert.Null(invocation.IdempotencyKey);
    }

    [Fact]
    public void AChange_CarriesEverythingTheOperatorSaid()
    {
        var (invocation, error) = DelegateArguments.Parse(
        [
            "Fix nginx", "--skill", "service.skill", "--capability", "service.restore", "--target", "web-1", "--environment", "prod",
            "--blast-radius", "Multiple", "--input", "{\"name\":\"nginx\"}", "--max-steps", "8", "--max-tokens", "40000",
            "--idempotency-key", "ticket-42", "--dry-run",
        ]);

        Assert.Null(error);
        var remediation = invocation!.Request!.Remediation!;
        Assert.Equal("service.skill", remediation.SkillId);
        Assert.Equal("service.restore", remediation.CapabilityName);
        Assert.Equal("web-1", remediation.Request.Target);
        Assert.Equal("prod", remediation.Request.Environment);
        Assert.Equal(BlastRadius.Multiple, remediation.Request.BlastRadius);
        Assert.True(remediation.Request.DryRun);
        Assert.Equal("nginx", remediation.Request.Input.GetRequired<string>("name"));
        Assert.Equal(8, invocation.Request.Authority!.MaxSteps);
        Assert.Equal(40000, invocation.Request.Authority.MaxTokens);
        Assert.Equal("ticket-42", invocation.IdempotencyKey);
    }

    [Fact]
    public void AChangeWithoutItsCompanions_IsRefusedRatherThanGuessed()
    {
        var (invocation, error) = DelegateArguments.Parse(["Fix it", "--skill", "service.skill", "--capability", "service.restore"]);

        Assert.Null(invocation);
        Assert.Contains("--skill, --capability, --target and --environment together", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status", "Status")]
    [InlineData("resume", "Resume")]
    [InlineData("cancel", "Cancel")]
    [InlineData("STATUS", "Status")]
    public void ARunCommand_TakesOneRunId(string verb, string expected)
    {
        var (invocation, error) = DelegateArguments.Parse([verb, RunId]);

        Assert.Null(error);
        Assert.Equal(Enum.Parse<DelegateAction>(expected), invocation!.Action);
        Assert.Equal(Guid.Parse(RunId), invocation.RunId);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("resume", "not-a-guid")]
    [InlineData("cancel", "00000000-0000-0000-0000-000000000000")]
    public void ARunCommand_WithoutAValidRunId_IsRefused(params string[] args)
    {
        var (invocation, error) = DelegateArguments.Parse(args);

        Assert.Null(invocation);
        Assert.Contains("takes one run id", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_NeedsExactlyOneDecision()
    {
        var (accepted, _) = DelegateArguments.Parse(["reconcile", RunId, "--accept", "--note", "checked by hand"]);
        var (abandoned, _) = DelegateArguments.Parse(["reconcile", RunId, "--abandon"]);
        var (neither, neitherError) = DelegateArguments.Parse(["reconcile", RunId]);
        var (both, bothError) = DelegateArguments.Parse(["reconcile", RunId, "--accept", "--abandon"]);

        Assert.Equal(ReconciliationAction.OperatorAcceptedDone, accepted!.Decision);
        Assert.Equal("checked by hand", accepted.Note);
        Assert.Equal(ReconciliationAction.OperatorAbandoned, abandoned!.Decision);
        Assert.Null(neither);
        Assert.Null(both);
        Assert.NotNull(neitherError);
        Assert.Contains("exactly one", bothError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Fix it", "--frobnicate")]
    [InlineData("Fix it", "--max-steps")]
    [InlineData("Fix it", "--max-steps", "0")]
    [InlineData("Fix it", "--max-tokens", "many")]
    [InlineData("Fix it", "--accept")]
    [InlineData("--dry-run")]
    public void ANonsenseCommandLine_IsRefusedWithAMessage(params string[] args)
    {
        var (invocation, error) = DelegateArguments.Parse(args);

        Assert.Null(invocation);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("--blast-radius", "galaxy")]
    [InlineData("--blast-radius", "5")]
    [InlineData("--input", "[1,2]")]
    [InlineData("--input", "not json")]
    public void ABadChangeOption_IsRefused(string option, string value)
    {
        var (invocation, error) = DelegateArguments.Parse(
            ["Fix it", "--skill", "s", "--capability", "c", "--target", "t", "--environment", "e", option, value]);

        Assert.Null(invocation);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void NothingToDelegate_IsRefused()
    {
        var (invocation, error) = DelegateArguments.Parse([]);

        Assert.Null(invocation);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(DelegationStatus.Completed, 0)]
    [InlineData(DelegationStatus.DiagnosisCompleted, 0)]
    [InlineData(DelegationStatus.Failed, 1)]
    [InlineData(DelegationStatus.Denied, 2)]
    [InlineData(DelegationStatus.PolicyBlocked, 2)]
    [InlineData(DelegationStatus.Rejected, 3)]
    [InlineData(DelegationStatus.Abandoned, 3)]
    [InlineData(DelegationStatus.RequiresReconciliation, 4)]
    [InlineData(DelegationStatus.BudgetExceeded, 5)]
    [InlineData(DelegationStatus.DeadlineExceeded, 5)]
    [InlineData(DelegationStatus.VerificationFailed, 6)]
    [InlineData(DelegationStatus.Running, 10)]
    [InlineData(DelegationStatus.AwaitingApproval, 10)]
    [InlineData(DelegationStatus.Cancelled, 130)]
    public void EveryStatusHasItsOwnExitCode(DelegationStatus status, int expected)
    {
        Assert.Equal(expected, DelegateCommand.ExitCodeFor(status));
    }

    [Fact]
    public void NoStatusIsLeftWithoutADeliberateExitCode_AndOnlyTheSuccessesAreZero()
    {
        foreach (var status in Enum.GetValues<DelegationStatus>())
        {
            var code = DelegateCommand.ExitCodeFor(status);
            Assert.Equal(status is DelegationStatus.Completed or DelegationStatus.DiagnosisCompleted, code == 0);
        }
    }
}
