// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Storage.Core;

/// <summary>
/// The typed evidence facts <c>storage.health</c> emits beside its output (ADR-0050; ADR-0052 AF-04), projected from the devices the
/// call matched, before the row limit and the byte budget cut anything. The shared contract carries each platform's own health
/// vocabulary without pretending parity: Windows reports <c>healthy</c>, <c>warning</c>, <c>unhealthy</c> and <c>unknown</c>; Linux
/// reports <c>healthy</c>, <c>critical</c> and <c>unknown</c>; a count exists only for a value actually observed, so an absent
/// category is never a zero fact. <c>unknown</c> is an observation of unknown health, never of health. Counts are of the devices
/// this call matched and are presented under the result's completeness (a device with partial reliability evidence makes the
/// result partial).
/// </summary>
public static class StorageHealthFacts
{
    /// <summary>The fact type: devices the call matched. The one key is <see cref="AllKey"/>.</summary>
    public const string DevicesObservedType = "storage.health.devices-observed";

    /// <summary>The fact type: devices reporting one health value. The key is that value.</summary>
    public const string DeviceCountType = "storage.health.device-count";

    /// <summary>The fact type: a device whose health needs follow-up; the key is the device identity and the value its health.</summary>
    public const string AttentionDeviceType = "storage.health.attention-device";

    /// <summary>The key of <see cref="DevicesObservedType"/>.</summary>
    public const string AllKey = "all";

    /// <summary>The most attention devices one call lists.</summary>
    public const int MaximumAttentionDevices = 6;

    private const int MaximumIdentifierCharacters = 128;
    private const int MaximumFacts = 16;
    private const int MaximumSerializedBytes = 4096;

    // Most severe first. Package-owned: the health values either platform's collector can report that call for follow-up.
    private static readonly string[] AttentionOrder = ["critical", "unhealthy", "warning"];

    /// <summary>The facts of one call, over the same device filter <see cref="StorageFormatting.Health"/> applies.</summary>
    public static IReadOnlyList<EvidenceFact> From(IReadOnlyList<StorageHealth> values, string? device)
    {
        ArgumentNullException.ThrowIfNull(values);
        var matched = values
            .Where(x => device is null || string.Equals(x.Device, device, StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(x.Health))
            .ToArray();
        if (matched.Length == 0)
        {
            return [];
        }

        var facts = new List<EvidenceFact> { Integer(DevicesObservedType, AllKey, matched.Length) };
        facts.AddRange(matched
            .Where(x => x.Health.Length <= MaximumIdentifierCharacters)
            .GroupBy(x => x.Health, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => Integer(DeviceCountType, group.Key, group.Count())));
        facts.AddRange(matched
            .Where(x => Array.IndexOf(AttentionOrder, x.Health) >= 0 && x.Device is { Length: > 0 and <= MaximumIdentifierCharacters })
            .OrderBy(x => Array.IndexOf(AttentionOrder, x.Health))
            .ThenBy(x => x.Device, StringComparer.Ordinal)
            .DistinctBy(x => x.Device, StringComparer.Ordinal)
            .Take(MaximumAttentionDevices)
            .Select(x => new EvidenceFact(AttentionDeviceType, x.Device, ToolParameterType.String, JsonValue.Create(x.Health)!)));

        while (facts.Count > 0 && (facts.Count > MaximumFacts || JsonSerializer.SerializeToUtf8Bytes(facts).Length > MaximumSerializedBytes))
        {
            facts.RemoveAt(facts.Count - 1);
        }

        return facts;
    }

    private static EvidenceFact Integer(string type, string key, int value) =>
        new(type, key, ToolParameterType.Integer, JsonValue.Create(value));
}
