// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

public sealed class JournalctlArgumentsTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 21, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 21, 12, 30, 45, TimeSpan.Zero);

    private static SystemEventQuery Query(Func<SystemEventQuery, SystemEventQuery>? change = null)
    {
        var query = new SystemEventQuery(From, To, null, null, null, null, null, 10_000);
        return change is null ? query : change(query);
    }

    [Fact]
    public void TheBaseArguments_AreFixedSwitchesAndAWindow()
    {
        Assert.Equal(
            [
                "--no-pager",
                "--output=json",
                "--reverse",
                "--utc",
                "--since=2026-09-21 11:00:00 UTC",
                "--until=2026-09-21 12:30:45 UTC",
                "--lines=10000",
                "--output-fields=MESSAGE,PRIORITY,SYSLOG_IDENTIFIER,_SYSTEMD_UNIT,_PID,_COMM,MESSAGE_ID,_TRANSPORT",
            ],
            JournalctlArguments.Build(Query()));
    }

    [Theory]
    [InlineData(SystemEventSeverity.Critical, "--priority=2")]
    [InlineData(SystemEventSeverity.Error, "--priority=3")]
    [InlineData(SystemEventSeverity.Warning, "--priority=4")]
    [InlineData(SystemEventSeverity.Information, "--priority=6")]
    public void MinimumSeverity_IsTheHighestPriorityNumberStillWanted(SystemEventSeverity minimum, string expected)
    {
        var arguments = JournalctlArguments.Build(Query(q => q with { MinSeverity = minimum }));

        Assert.Contains(expected, arguments);
        Assert.Single(arguments, a => a.StartsWith("--priority", StringComparison.Ordinal));
    }

    [Fact]
    public void VerboseAsTheMinimum_NeedsNoPriorityFilter_BecauseEverythingQualifies() =>
        Assert.DoesNotContain(
            JournalctlArguments.Build(Query(q => q with { MinSeverity = SystemEventSeverity.Verbose })),
            a => a.StartsWith("--priority", StringComparison.Ordinal));

    [Fact]
    public void NoSeverity_MeansNoPriorityFilter() =>
        Assert.DoesNotContain(JournalctlArguments.Build(Query()), a => a.StartsWith("--priority", StringComparison.Ordinal));

    [Fact]
    public void ChannelAndEventId_BecomeTwoJournalMatches_InLowerCase()
    {
        var arguments = JournalctlArguments.Build(Query(q => q with { Channel = "KERNEL", EventId = "39F53479D3A045AC8E11786248231FBF" }));

        Assert.Equal("_TRANSPORT=kernel", arguments[^2]);
        Assert.Equal("MESSAGE_ID=39f53479d3a045ac8e11786248231fbf", arguments[^1]);
    }

    [Fact]
    public void SourceAndText_NeverReachTheCommandLine()
    {
        var arguments = JournalctlArguments.Build(Query(q => q with { Source = "nginx.service", Text = "$(touch /tmp/x); rm -rf /" }));

        Assert.DoesNotContain(arguments, a => a.Contains("nginx", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.Contains("touch", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.Contains("rm ", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryArgument_IsEitherAFixedSwitchOrAKnownFieldMatch()
    {
        var arguments = JournalctlArguments.Build(Query(q => q with
        {
            MinSeverity = SystemEventSeverity.Error,
            Channel = "kernel",
            EventId = "39f53479d3a045ac8e11786248231fbf",
            Source = "x",
            Text = "y",
        }));

        Assert.All(arguments, argument => Assert.True(
            argument.StartsWith("--", StringComparison.Ordinal)
            || argument.StartsWith("_TRANSPORT=", StringComparison.Ordinal)
            || argument.StartsWith("MESSAGE_ID=", StringComparison.Ordinal),
            argument));
    }

    [Fact]
    public void TheTimeIsAlwaysUtc_WhateverTheOffsetOfTheQuery()
    {
        var query = new SystemEventQuery(
            new DateTimeOffset(2026, 9, 21, 13, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 21, 14, 0, 0, TimeSpan.FromHours(2)),
            null, null, null, null, null, 10_000);

        var arguments = JournalctlArguments.Build(query);

        Assert.Contains("--since=2026-09-21 11:00:00 UTC", arguments);
        Assert.Contains("--until=2026-09-21 12:00:00 UTC", arguments);
    }

    [Fact]
    public void TheLineCeiling_IsTheQueryScanCeiling() =>
        Assert.Contains("--lines=25", JournalctlArguments.Build(Query(q => q with { ScanCeiling = 25 })));
}
