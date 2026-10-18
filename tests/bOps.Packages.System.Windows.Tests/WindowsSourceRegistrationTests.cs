// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Eventing.Reader;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Conformance;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// ADR-0061 on Windows: the provider catalog answers "is this exact name registered?" as a tri-state (registered, not registered,
/// unknown), is read once per call, and leaves the native query exactly as it was.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsSourceRegistrationTests
{
    private static JsonObject Run(WindowsSystemEventsTool tool, Action<JsonObject> configure)
    {
        var json = new JsonObject { ["windowMinutes"] = 1 };
        configure(json);
        var result = tool.ExecuteAsync(ToolArguments.FromJson(json)).GetAwaiter().GetResult();
        Assert.True(result.Succeeded, result.ErrorMessage);
        return JsonNode.Parse(result.Output!)!.AsObject();
    }

    private static string UnusedName() => "bOps-no-such-source-" + Guid.NewGuid().ToString("N");

    // ---- the pure classification ----

    [Theory]
    [InlineData("service control manager", SourceRegistration.Registered, "Service Control Manager")]
    [InlineData("SERVICE CONTROL MANAGER", SourceRegistration.Registered, "Service Control Manager")]
    [InlineData("Service Control", SourceRegistration.NotRegistered, null)]
    [InlineData("Service Control Manager ", SourceRegistration.NotRegistered, null)]
    public void TheName_IsRegistered_OnlyOnAnExactMatchIgnoringCase(string source, SourceRegistration expected, string? spelling)
    {
        var (registration, registered) = WindowsSystemEventsTool.ClassifySource(source, ["Disk", "Service Control Manager", ".NET Runtime"]);

        Assert.Equal(expected, registration);
        Assert.Equal(spelling, registered);
    }

    [Fact]
    public void ACatalogThatCouldNotBeRead_OrIsEmpty_LeavesTheNameUnknown()
    {
        Assert.Equal(SourceRegistration.Unknown, WindowsSystemEventsTool.ClassifySource("Disk", null).Registration);
        Assert.Equal(SourceRegistration.Unknown, WindowsSystemEventsTool.ClassifySource("Disk", []).Registration);
    }

    [Fact]
    public void TheCatalogRead_FailsSoftlyOnTheTwoExpectedExceptions_AndNothingElse()
    {
        Assert.Null(WindowsSystemEventsTool.ReadCatalog(() => throw new EventLogException("denied")));
        Assert.Null(WindowsSystemEventsTool.ReadCatalog(() => throw new UnauthorizedAccessException()));
        Assert.Equal(["a", "b"], WindowsSystemEventsTool.ReadCatalog(() => ["a", "b"]));
        Assert.Throws<InvalidOperationException>(() => WindowsSystemEventsTool.ReadCatalog(() => throw new InvalidOperationException()));
    }

    // ---- through the tool, with a controlled catalog ----

    [Fact]
    public void TheCatalog_IsReadOnce_ForTheSourceAndTheExclusions()
    {
        var reads = 0;
        var tool = new WindowsSystemEventsTool(TimeProvider.System, TimeSpan.FromSeconds(20), () =>
        {
            reads++;
            return ["Service Control Manager", "Disk"];
        });

        var json = Run(tool, j =>
        {
            j["source"] = "disk";
            j["excludeSources"] = "Service Control Manager";
        });

        Assert.Equal(1, reads);
        Assert.Equal("registered", json["sourceFilter"]!["registrationStatus"]!.GetValue<string>());
    }

    [Fact]
    public void WithoutASourceOrExclusions_TheCatalogIsNotRead_AndTheFilterIsNull()
    {
        var reads = 0;
        var tool = new WindowsSystemEventsTool(TimeProvider.System, TimeSpan.FromSeconds(20), () =>
        {
            reads++;
            return ["Disk"];
        });

        var json = Run(tool, _ => { });

        Assert.Equal(0, reads);
        Assert.Null(json["sourceFilter"]);
    }

    [Fact]
    public void AFailedCatalog_IsUnknown_ChangesNoCompleteness_AndNamesTheCautionWhenNothingMatched()
    {
        var name = UnusedName();
        var failing = new WindowsSystemEventsTool(TimeProvider.System, TimeSpan.FromSeconds(20), () => null);
        var working = new WindowsSystemEventsTool(TimeProvider.System, TimeSpan.FromSeconds(20), () => ["Disk"]);

        var unknown = Run(failing, j => j["source"] = name);
        var notRegistered = Run(working, j => j["source"] = name);

        Assert.Equal("unknown", unknown["sourceFilter"]!["registrationStatus"]!.GetValue<string>());
        Assert.Equal("noMatchSourceUnverified", unknown["sourceFilter"]!["caution"]!.GetValue<string>());
        Assert.Equal("notRegistered", notRegistered["sourceFilter"]!["registrationStatus"]!.GetValue<string>());
        Assert.Equal("noMatchSourceNotRegistered", notRegistered["sourceFilter"]!["caution"]!.GetValue<string>());
        Assert.False(unknown["sourceFilter"]!["observedMatch"]!.GetValue<bool>());
        Assert.Equal(["source"], unknown["sourceFilter"]!["matchedFields"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
        Assert.Equal(unknown["status"]!.GetValue<string>(), notRegistered["status"]!.GetValue<string>());
        Assert.Equal(unknown["truncated"]!.GetValue<bool>(), notRegistered["truncated"]!.GetValue<bool>());
    }

    // ---- against the real Event Log ----

    [Fact]
    public void ANameTakenFromTheLiveCatalog_IsRegistered_AndAnImpossibleOneIsNot()
    {
        using var session = new EventLogSession();
        var live = session.GetProviderNames().First(name => name.Length <= SystemEventsLimits.SourceCharacters && name.All(character =>
            char.IsAsciiLetterOrDigit(character) || " _.@:/()-".Contains(character, StringComparison.Ordinal)));
        var tool = new WindowsSystemEventsTool();

        var registered = Run(tool, j =>
        {
            j["source"] = live.ToUpperInvariant();
            j["eventId"] = "65535";
        });
        var absent = Run(tool, j => j["source"] = UnusedName());

        Assert.Equal("registered", registered["sourceFilter"]!["registrationStatus"]!.GetValue<string>());
        Assert.Null(registered["sourceFilter"]!["caution"]);
        Assert.Equal("notRegistered", absent["sourceFilter"]!["registrationStatus"]!.GetValue<string>());
        Assert.False(absent["sourceFilter"]!["observedMatch"]!.GetValue<bool>());
        Assert.Equal("noMatchSourceNotRegistered", absent["sourceFilter"]!["caution"]!.GetValue<string>());
        Assert.Equal(0, absent["observedEvents"]!.GetValue<int>());
    }

    [Fact]
    public Task TheSourceFilter_ConformsAgainstTheRealEventLog() =>
        SystemToolConformance.AssertSystemEventsSourceFilterConformAsync(new WindowsSystemEventsTool(), "notRegistered", ["source"]);

    [Fact]
    public Task TheLargestEnvelope_FitsTheSmallestBudget_AgainstTheRealEventLog() =>
        SystemToolConformance.AssertSystemEventsEnvelopeFitsTheSmallestBudgetAsync(new WindowsSystemEventsTool());
}
