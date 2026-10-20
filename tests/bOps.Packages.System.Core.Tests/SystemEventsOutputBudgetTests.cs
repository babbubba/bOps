// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using Xunit;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// The <c>maxOutputBytes</c> budget of <c>system.events</c> measured on the serialized UTF-8 bytes (ADR-0032 HARDEN-9 amendment §4,
/// ADR-0061 §2.8): the largest envelope the argument grammar can produce fits the smallest budget whatever text the host reports, the
/// budget only ever removes rows or groups, and an envelope that cannot fit is refused instead of being cut or declared complete.
/// </summary>
public sealed class SystemEventsOutputBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The ceiling ADR-0061 §2.8 sets for the envelope alone at the 4,096-byte minimum; above it, implementation returns to architecture.</summary>
    private const int EnvelopeCeiling = 3_584;

    /// <summary>The longest legal <c>excludeSources</c>: eight entries, 1,024 characters in all, separators included (the grammar is ASCII only).</summary>
    private static readonly string[] MaximalExclusions =
        [.. Enumerable.Range(0, 7).Select(i => new string((char)('a' + i), 128)), new string('z', 121)];

    private static readonly string MaximalSource = new('s', SystemEventsLimits.SourceCharacters);

    public static TheoryData<string> DetailAlphabets => new()
    {
        "d",            // plain ASCII
        "'",            // ASCII that the default encoder writes as six bytes (')
        "\"",           // likewise
        "è",            // two UTF-8 bytes, six escaped
        "漢",           // three UTF-8 bytes, six escaped
        "😀",           // four UTF-8 bytes, twelve escaped (a surrogate pair)
        "a'è漢😀\\\n",  // a mixture, with a backslash and a line feed
    };

    public static TheoryData<EvidenceMode, int> ModesAndBudgets
    {
        get
        {
            var data = new TheoryData<EvidenceMode, int>();
            foreach (var mode in new[] { EvidenceMode.Raw, EvidenceMode.Aggregate })
            {
                foreach (var budget in new[] { SystemEventsLimits.MinimumOutputBytes, 6_144, 8_192, SystemEventsLimits.DefaultOutputBytes, SystemEventsLimits.MaximumOutputBytes })
                {
                    data.Add(mode, budget);
                }
            }

            return data;
        }
    }

    private static string Repeat(string unit, int utf16Units)
    {
        var builder = new StringBuilder();
        while (builder.Length + unit.Length <= utf16Units)
        {
            builder.Append(unit);
        }

        return builder.ToString();
    }

    private static SystemEventRecord Event(int index, string message) =>
        new(Now.AddMinutes(-(index % 55)), SystemEventSeverity.Error, MaximalSource, index.ToString(), index % 2 == 0 ? "System" : "Application", message, 42, "proc", null);

    /// <summary>Two default Windows channels, each with a source detail of the given text and a coverage store with the largest log size.</summary>
    private static SystemEventSnapshot Snapshot(string detail, int events, string message) =>
        new(
            Enumerable.Range(0, events).Select(index => Event(index, message)).ToArray(),
            [
                new InventorySourceResult("windows.channel.System", InventorySourceStatus.Unavailable, detail) { ExaminedFromUtc = Now.AddMinutes(-50) },
                new InventorySourceResult("windows.channel.Application", InventorySourceStatus.Unavailable, detail) { ExaminedFromUtc = Now.AddMinutes(-50) },
            ],
            CollectionTruncated: true)
        {
            Stores =
            [
                new CoverageStore("windows.channel.System", CoverageBasis.EventLog, Now.AddMinutes(-30), long.MaxValue, ["windows.channel.System"]),
                new CoverageStore("windows.channel.Application", CoverageBasis.EventLog, Now.AddMinutes(-30), long.MaxValue, ["windows.channel.Application"]),
            ],
            SourceRegistration = SourceRegistration.NotRegistered,
        };

    private static SystemEventQuery MaximalQuery(string? source = null, IReadOnlyList<string>? excluded = null) =>
        new(Now.AddDays(-180), Now, null, source ?? MaximalSource, null, null, null, SystemEventsLimits.ScanCeiling, excluded ?? MaximalExclusions);

    private static int Bytes(string output) => Encoding.UTF8.GetByteCount(output);

    [Theory]
    [MemberData(nameof(DetailAlphabets))]
    public void TheLargestEnvelope_WhateverTheHostWritesInADetail_StaysWithinTheCeilingOfAdr0061(string alphabet)
    {
        ArgumentNullException.ThrowIfNull(alphabet);
        var snapshot = Snapshot(Repeat(alphabet, 300), 0, "m");

        foreach (var mode in new[] { EvidenceMode.Raw, EvidenceMode.Aggregate })
        {
            var bytes = Bytes(SystemEventFormatting.Format(snapshot, MaximalQuery(), mode, 500, SystemEventsLimits.MaximumOutputBytes));

            Assert.True(bytes <= EnvelopeCeiling, $"{mode}: the envelope alone is {bytes} bytes (ceiling {EnvelopeCeiling}).");
        }
    }

    [Theory]
    [MemberData(nameof(DetailAlphabets))]
    public void AtTheSmallestBudget_WithEveryMaximumAtOnce_OnlyRowsOrGroupsAreCut_AndNothingIsDeclaredComplete(string alphabet)
    {
        ArgumentNullException.ThrowIfNull(alphabet);
        var snapshot = Snapshot(Repeat(alphabet, 300), 200, Repeat(alphabet, 2_000));

        foreach (var mode in new[] { EvidenceMode.Raw, EvidenceMode.Aggregate })
        {
            var output = SystemEventFormatting.Format(snapshot, MaximalQuery(), mode, 500, SystemEventsLimits.MinimumOutputBytes);

            Assert.True(Bytes(output) <= SystemEventsLimits.MinimumOutputBytes, $"{mode}: {Bytes(output)} bytes.");
            var json = JsonNode.Parse(output)!.AsObject();
            Assert.Equal(MaximalExclusions.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), json["excludeSources"]!.AsArray().Select(item => item!.GetValue<string>()));
            Assert.Equal(MaximalSource, json["sourceFilter"]!["requested"]!.GetValue<string>());
            Assert.Equal(2, json["sources"]!.AsArray().Count);
            Assert.Equal(2, json["coverage"]!["stores"]!.AsArray().Count);
            Assert.Equal(200, json["observedEvents"]!.GetValue<int>());
            Assert.True(json["sourceFilter"]!["observedMatch"]!.GetValue<bool>());
            Assert.True(json["truncated"]!.GetValue<bool>());
            Assert.False(json["complete"]!.GetValue<bool>());
        }
    }

    [Theory]
    [MemberData(nameof(ModesAndBudgets))]
    public void EveryBudget_ProducesValidJsonWithinTheBudget_AndStatesWhatTheBudgetCut(EvidenceMode mode, int budget)
    {
        // Escape-heavy messages: a 512-character sample costs 3,072 bytes in an aggregate group and 12,000 in a raw row.
        var snapshot = Snapshot("It's \"denied\"", 300, Repeat("\"é漢", 2_000));

        var output = SystemEventFormatting.Format(snapshot, MaximalQuery(), mode, 500, budget);

        Assert.True(Bytes(output) <= budget, $"{Bytes(output)} bytes for a budget of {budget}.");
        var json = JsonNode.Parse(output)!.AsObject();
        var entries = json[mode == EvidenceMode.Raw ? "events" : "groups"]!.AsArray();
        Assert.Equal(entries.Count, json[mode == EvidenceMode.Raw ? "returnedEvents" : "returnedGroups"]!.GetValue<int>());
        Assert.Equal(300, json["observedEvents"]!.GetValue<int>());
        Assert.True(json["sourceFilter"]!["observedMatch"]!.GetValue<bool>());
        if (entries.Count < (mode == EvidenceMode.Raw ? 300 : json["observedGroups"]!.GetValue<int>()))
        {
            Assert.True(json["truncated"]!.GetValue<bool>());
        }

        Assert.False(json["complete"]!.GetValue<bool>());
        var keys = json.Select(pair => pair.Key).ToList();
        Assert.Equal(keys.IndexOf("excludeSources") + 1, keys.IndexOf("sourceFilter"));
    }

    [Theory]
    [MemberData(nameof(ModesAndBudgets))]
    public void TheBudget_IsMeasuredOnUtf8Bytes_NotOnCharacters(EvidenceMode mode, int budget)
    {
        var snapshot = Snapshot("x", 300, Repeat("漢", 2_000));

        var output = SystemEventFormatting.Format(snapshot, MaximalQuery(excluded: ["Nginx"]), mode, 500, budget);

        Assert.True(Bytes(output) <= budget);
        Assert.True(Bytes(output) >= output.Length);
        Assert.Equal(output, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(output)));
    }

    [Fact]
    public void ADetailWithinBothBounds_IsReturnedUnchanged()
    {
        const string plain = "The channel could not be read: access is denied.";

        Assert.Equal(plain, SystemEventFormatting.BoundedDetail(plain));
        Assert.Null(SystemEventFormatting.BoundedDetail(null));
        Assert.Equal(string.Empty, SystemEventFormatting.BoundedDetail(string.Empty));
        Assert.Equal(new string('d', 256), SystemEventFormatting.BoundedDetail(new string('d', 256)));
        Assert.Equal(new string('d', 253) + "...", SystemEventFormatting.BoundedDetail(new string('d', 300)));
        Assert.Equal(new string('d', 253) + "...", SystemEventFormatting.BoundedDetail(new string('d', 257)));
    }

    [Theory]
    [MemberData(nameof(DetailAlphabets))]
    public void ADetail_IsCutOnASerializedByteBound_NeverInsideACodePoint_AndDeterministically(string alphabet)
    {
        ArgumentNullException.ThrowIfNull(alphabet);
        var detail = Repeat(alphabet, 300);

        var bounded = SystemEventFormatting.BoundedDetail(detail)!;

        var serialized = JsonValue.Create(bounded)!.ToJsonString();
        Assert.True(Encoding.UTF8.GetByteCount(serialized) - 2 <= SystemEventFormatting.SourceDetailBytes, $"{serialized.Length} serialized bytes.");
        Assert.True(bounded.Length <= 256);
        Assert.EndsWith("...", bounded, StringComparison.Ordinal);
        Assert.StartsWith(bounded[..^3], detail, StringComparison.Ordinal);
        Assert.DoesNotContain('�', bounded);
        Assert.True(bounded.EnumerateRunes().All(rune => rune.Value != 0xFFFD), "a code point was split");
        Assert.Equal(bounded, SystemEventFormatting.BoundedDetail(detail));
        Assert.NotEmpty(bounded);
    }

    [Fact]
    public void ACutDetail_KeepsItsLeadingReason_AndSaysItWasCut()
    {
        var detail = "Access to the log is denied for this identity. " + Repeat("è", 400);

        var bounded = SystemEventFormatting.BoundedDetail(detail)!;

        Assert.StartsWith("Access to the log is denied for this identity. ", bounded, StringComparison.Ordinal);
        Assert.EndsWith("...", bounded, StringComparison.Ordinal);
        Assert.NotEqual(detail, bounded);
    }

    [Theory]
    [MemberData(nameof(DetailAlphabets))]
    public void EvenAThirdSource_WithTheLargestDetail_LeavesTheEnvelopeWithinTheSmallestBudget(string alphabet)
    {
        ArgumentNullException.ThrowIfNull(alphabet);
        // Windows reads at most two channels, Linux one; a third source is headroom, not a reachable case.
        var detail = Repeat(alphabet, 300);
        var two = Snapshot(detail, 0, "m");
        var snapshot = two with { Sources = [.. two.Sources, new InventorySourceResult("windows.channel.Extra", InventorySourceStatus.Unavailable, detail)] };

        foreach (var mode in new[] { EvidenceMode.Raw, EvidenceMode.Aggregate })
        {
            var bytes = Bytes(SystemEventFormatting.Format(snapshot, MaximalQuery(), mode, 500, SystemEventsLimits.MinimumOutputBytes));

            Assert.True(bytes <= SystemEventsLimits.MinimumOutputBytes, $"{mode}: {bytes} bytes.");
        }
    }

    [Fact]
    public void ADetail_IsNeverCutInsideASurrogatePair()
    {
        // 'x' shifts every pair onto an odd index, so a cut that ignores code points would leave a lone high surrogate.
        for (var length = 1; length < 80; length++)
        {
            var detail = "x" + string.Concat(Enumerable.Repeat("😀", length));

            var bounded = SystemEventFormatting.BoundedDetail(detail)!;

            var kept = bounded.EndsWith("...", StringComparison.Ordinal) ? bounded[..^3] : bounded;
            Assert.False(char.IsHighSurrogate(kept[^1]), $"{length} pairs: a lone high surrogate was left.");
            Assert.True(bounded.EnumerateRunes().All(rune => rune.Value != 0xFFFD));
        }
    }

    [Fact]
    public void TheDetailOfARecordedSource_ReachesTheResultBounded_AndTheStatusIsUntouched()
    {
        var snapshot = Snapshot(Repeat("漢", 300), 0, "m");

        var json = JsonNode.Parse(SystemEventFormatting.Format(snapshot, MaximalQuery(), EvidenceMode.Raw, 10, SystemEventsLimits.MinimumOutputBytes))!.AsObject();

        foreach (var source in json["sources"]!.AsArray())
        {
            Assert.Equal("unavailable", source!["status"]!.GetValue<string>());
            Assert.True(source["detail"]!.GetValue<string>().Length is > 3 and <= 45);
            Assert.EndsWith("...", source["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.Equal("unavailable", json["status"]!.GetValue<string>());
        Assert.False(json["complete"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("é")]
    [InlineData("漢")]
    [InlineData("😀")]
    public void TheArgumentGrammar_RefusesNonAsciiNames_SoTheEchoCanNeverBeWiderThanAnAsciiOne(string unit)
    {
        foreach (var field in new[] { "source", "excludeSources" })
        {
            var arguments = ToolArguments.FromJson(new JsonObject { [field] = "name" + unit });

            Assert.False(SystemEventsArguments.TryRead(arguments, Now, out _, out _, out _, out _, out var error));
            Assert.Contains(field, error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(EvidenceMode.Raw)]
    [InlineData(EvidenceMode.Aggregate)]
    public void AnEnvelopeThatCannotFitTheBudget_IsRefused_NeverCut_NorReturnedOverBudget(EvidenceMode mode)
    {
        // Outside the grammar (a caller that bypasses argument validation): 1,016 multibyte characters in the echo cost over 6,000 bytes.
        var excluded = Enumerable.Range(0, 8).Select(i => Repeat("漢", 127) + (char)('a' + i)).ToArray();
        var snapshot = Snapshot("x", 10, "m");

        var failure = Assert.Throws<InvalidOperationException>(
            () => SystemEventFormatting.Format(snapshot, MaximalQuery(excluded: excluded), mode, 500, SystemEventsLimits.MinimumOutputBytes));

        Assert.Contains("4096-byte", failure.Message, StringComparison.Ordinal);
        // With room for it, the same echo is returned whole and unaltered.
        var json = JsonNode.Parse(SystemEventFormatting.Format(snapshot, MaximalQuery(excluded: excluded), mode, 500, SystemEventsLimits.MaximumOutputBytes))!.AsObject();
        Assert.Equal(excluded.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), json["excludeSources"]!.AsArray().Select(item => item!.GetValue<string>()));
    }
}
