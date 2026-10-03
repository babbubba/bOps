// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// HARDEN-9 / HARDEN-7 residual N-2 (ADR-0042 §16): the <c>system.crashes</c> manifest description states, unambiguously, that a
/// <c>BlueScreen</c> (kernel-bugcheck) Report.wer <c>EventTime</c> is <c>reported</c> and not an occurrence time, while the other report
/// kinds stay <c>occurred</c>. Wording only: no argument, field or schema version changes, and both platforms keep one contract.
/// </summary>
public sealed class HardenNineManifestTests
{
    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    public void TheCrashesDescription_StatesTheBlueScreenReportedException(string platform)
    {
        var description = SystemToolManifests.Crashes(platform).Description;

        Assert.Contains("The Report.wer EventTime of a BlueScreen (kernel-bugcheck) report is reported, not an occurrence time", description, StringComparison.Ordinal);
        Assert.Contains("because it is written after the restart", description, StringComparison.Ordinal);
        Assert.Contains("never read a reported time as the crash time", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCrashesDescription_NoLongerCallsEveryReportWerEventTimeAnOccurrenceTime()
    {
        var description = SystemToolManifests.Crashes("windows").Description;

        // The unqualified claim of schema 2 as first written: "(Report.wer EventTime, Application Error record time)".
        Assert.DoesNotContain("proven occurrence time (Report.wer EventTime, Application Error record time)", description, StringComparison.Ordinal);
        Assert.Contains("the Report.wer EventTime of an application crash, hang or kernel live dump", description, StringComparison.Ordinal);
        Assert.Contains("the Application Error record time", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCrashesManifest_KeepsItsParametersAndItsSchemaVersion()
    {
        var manifest = SystemToolManifests.Crashes("windows");

        Assert.Equal(["mode", "sinceMinutes", "sinceDays", "limit"], manifest.Parameters.Select(p => p.Name));
        Assert.Equal(2, SystemCrashFormatting.SchemaVersion);
        Assert.Contains("schemaVersion 2", manifest.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowsAndLinuxCrashesManifests_AreTheSameContract_ApartFromThePlatform()
    {
        var windows = SystemToolManifests.Crashes("windows");
        var linux = SystemToolManifests.Crashes("linux");

        Assert.Equal(windows.Name, linux.Name);
        Assert.Equal(windows.Description, linux.Description);
        Assert.Equal(windows.Risk, linux.Risk);
        Assert.Equal(windows.Parameters, linux.Parameters);
        Assert.Equal(["windows"], windows.Platforms);
        Assert.Equal(["linux"], linux.Platforms);
    }

    [Fact]
    public void TheEventsDescription_StillLabelsRecordTimesAsRecordedLaterThanTheIncident()
    {
        // A system.events record time is never an occurrence time (ADR-0042 §8); the manifest says so.
        var description = SystemToolManifests.Events("windows").Description;

        Assert.Contains("Times are record times", description, StringComparison.Ordinal);
        Assert.Contains("the record is written later than the incident it describes", description, StringComparison.Ordinal);
    }
}
