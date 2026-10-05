// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Cli.Tests;

/// <summary>ADR-0044 section 12: the reserved verbs <c>readiness</c> and <c>profiles</c>, and the strict <c>--input</c>.</summary>
public sealed class DelegateSetupArgumentsTests
{
    private static DelegateInvocation Parsed(params string[] args)
    {
        var (invocation, error) = DelegateArguments.Parse(args);
        Assert.True(invocation is not null, error);
        return invocation!;
    }

    private static string Refused(params string[] args)
    {
        var (invocation, error) = DelegateArguments.Parse(args);
        Assert.Null(invocation);
        return error!;
    }

    [Fact]
    public void Readiness_DefaultsToADiagnosis_AndTakesOnlyRemediation()
    {
        Assert.Equal(DelegateAction.Readiness, Parsed("readiness").Action);
        Assert.False(Parsed("readiness").Remediation);
        Assert.True(Parsed("readiness", "--remediation").Remediation);
        Assert.True(Parsed("READINESS", "--Remediation").Remediation);
        Assert.Contains("only --remediation", Refused("readiness", "--write"), StringComparison.Ordinal);
        Assert.True(Parsed("readiness").NeedsNoModel);
    }

    [Fact]
    public void ProfilesInit_RequiresReadOnly_AndOverwriteOnlyWithWrite()
    {
        var print = Parsed("profiles", "init", "--read-only");
        var write = Parsed("profiles", "init", "--read-only", "--write");
        var overwrite = Parsed("profiles", "init", "--write", "--overwrite", "--read-only");

        Assert.Equal(DelegateAction.ProfilesInit, print.Action);
        Assert.False(print.Write);
        Assert.True(write.Write);
        Assert.False(write.Overwrite);
        Assert.True(overwrite.Write && overwrite.Overwrite);
        Assert.True(print.NeedsNoModel);
        Assert.Contains("--read-only", Refused("profiles", "init"), StringComparison.Ordinal);
        Assert.Contains("--read-only", Refused("profiles", "init", "--write"), StringComparison.Ordinal);
        Assert.Contains("only with --write", Refused("profiles", "init", "--read-only", "--overwrite"), StringComparison.Ordinal);
        Assert.Contains("does not take", Refused("profiles", "init", "--read-only", "--remediation"), StringComparison.Ordinal);
        Assert.Contains("does not take", Refused("profiles", "init", "--read-only", "--write", "--write"), StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilesCheck_TakesNoOptions_AndProfilesNeedsASubcommand()
    {
        Assert.Equal(DelegateAction.ProfilesCheck, Parsed("profiles", "check").Action);
        Assert.True(Parsed("profiles", "check").NeedsNoModel);
        Assert.Contains("no options", Refused("profiles", "check", "--write"), StringComparison.Ordinal);
        Assert.Contains("'init --read-only' or 'check'", Refused("profiles"), StringComparison.Ordinal);
        Assert.Contains("'init --read-only' or 'check'", Refused("profiles", "list"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnObjectiveIsStillAStart_AndRunCommandsStillNeedAModel()
    {
        Assert.Equal(DelegateAction.Start, Parsed("Why is the readiness probe failing?").Action);
        Assert.False(Parsed("Why is it slow?").NeedsNoModel);
        Assert.False(Parsed("status", Guid.NewGuid().ToString()).NeedsNoModel);
    }

    [Fact]
    public void Input_WithARepeatedKey_IsRefused_NeverFirstOrLastWins()
    {
        var error = Refused("Fix it", "--skill", "s", "--capability", "c", "--target", "local", "--environment", "test", "--input", "{\"a\":1,\"a\":2}");

        Assert.Contains("no property repeated", error, StringComparison.Ordinal);
        Assert.NotNull(Parsed("Fix it", "--skill", "s", "--capability", "c", "--target", "local", "--environment", "test", "--input", "{\"a\":1,\"b\":{\"a\":2}}").Request!.Remediation);
        Assert.Contains("no property repeated", Refused("Fix it", "--skill", "s", "--capability", "c", "--target", "local", "--environment", "test", "--input", "{\"b\":{\"a\":1,\"a\":2}}"), StringComparison.Ordinal);
    }
}
