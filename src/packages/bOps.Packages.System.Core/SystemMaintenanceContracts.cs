// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

public static class SystemMaintenanceLimits
{
    public const int DefaultUpdates = 200, MaximumUpdates = 2000;
    public const int DefaultSinceDays = 30, MaximumSinceDays = 365;
    public const int DefaultHistory = 200, MaximumHistory = 2000;
    public const int DefaultSinceMinutes = 1440, MaximumSinceMinutes = 10080;
    public const int DefaultCrashes = 100, MaximumCrashes = 1000;
    public const int DefaultDrivers = 300, MaximumDrivers = 3000;
    public const int SourceCharacters = 128, WarningCharacters = 256, MaximumWarnings = 32;
    public const int NameCharacters = 512, TextCharacters = 1024, PathCharacters = 2048;
}

public sealed record MaintenanceArguments(string? Kind, int? SinceDays, int? SinceMinutes, int Limit);
public sealed record MaintenanceSource(string Name, InventorySourceStatus Status, string? Detail = null);
public sealed record UpdateRecord(string Id, string Name, string? CurrentVersion, string? AvailableVersion, string? Kind, bool? RebootMayBeRequired, string Source, DateTimeOffset? PublishedUtc);
public sealed record UpdateHistoryRecord(DateTimeOffset TimestampUtc, string Id, string Name, string? Version, string Result, string Source);
public sealed record CrashRecord(DateTimeOffset TimestampUtc, string? Process, int? Pid, string? Kind, string? DumpPath, string? EventIdOrCrashId, string? Summary, string Source);
public sealed record DriverRecord(string Name, string? PathOrModule, string? Version, string? Vendor, string? State, bool? Loaded, string? AddressOrSize, string Source);
public sealed record MaintenanceSnapshot<T>(IReadOnlyList<T> Items, IReadOnlyList<MaintenanceSource> Sources, IReadOnlyList<string>? Warnings = null, bool CollectionTruncated = false, TimeSpan? CatalogAge = null);

/// <summary>Shared argument validation. Invalid values are rejected, never silently clamped.</summary>
public static class SystemMaintenanceArguments
{
    public static readonly IReadOnlyList<string> UpdateKinds = ["all", "security", "critical", "other"];

    public static bool TryReadUpdates(ToolArguments args, out MaintenanceArguments value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        value = new(null, null, null, SystemMaintenanceLimits.DefaultUpdates);
        if (!TryLimit(args, "limit", SystemMaintenanceLimits.DefaultUpdates, SystemMaintenanceLimits.MaximumUpdates, out var limit, out error)) return false;
        string? kind = null;
        var json = args.ToJson();
        if (json.ContainsKey("kind") && json["kind"] is not null)
        {
            if (!args.TryGet<string>("kind", out kind) || !UpdateKinds.Contains(kind, StringComparer.Ordinal)) { error = "kind must be one of: all, security, critical, other."; return false; }
        }
        value = new(kind ?? "all", null, null, limit); error = null; return true;
    }

    public static bool TryReadHistory(ToolArguments args, out MaintenanceArguments value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        value = new(null, SystemMaintenanceLimits.DefaultSinceDays, null, SystemMaintenanceLimits.DefaultHistory);
        if (!TryLimit(args, "limit", SystemMaintenanceLimits.DefaultHistory, SystemMaintenanceLimits.MaximumHistory, out var limit, out error)
            || !TryInteger(args, "sinceDays", SystemMaintenanceLimits.DefaultSinceDays, SystemMaintenanceLimits.MaximumSinceDays, out var days, out error)) return false;
        value = new(null, days, null, limit); error = null; return true;
    }

    public static bool TryReadCrashes(ToolArguments args, out MaintenanceArguments value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        value = new(null, null, SystemMaintenanceLimits.DefaultSinceMinutes, SystemMaintenanceLimits.DefaultCrashes);
        if (!TryLimit(args, "limit", SystemMaintenanceLimits.DefaultCrashes, SystemMaintenanceLimits.MaximumCrashes, out var limit, out error)
            || !TryInteger(args, "sinceMinutes", SystemMaintenanceLimits.DefaultSinceMinutes, SystemMaintenanceLimits.MaximumSinceMinutes, out var minutes, out error)) return false;
        value = new(null, null, minutes, limit); error = null; return true;
    }

    public static bool TryReadDrivers(ToolArguments args, out MaintenanceArguments value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        value = new(null, null, null, SystemMaintenanceLimits.DefaultDrivers);
        if (!TryLimit(args, "limit", SystemMaintenanceLimits.DefaultDrivers, SystemMaintenanceLimits.MaximumDrivers, out var limit, out error)) return false;
        value = new(null, null, null, limit); error = null; return true;
    }

    private static bool TryLimit(ToolArguments args, string name, int fallback, int maximum, out int value, out string? error) => TryInteger(args, name, fallback, maximum, out value, out error);
    private static bool TryInteger(ToolArguments args, string name, int fallback, int maximum, out int value, out string? error)
    {
        value = fallback; error = null;
        var json = args.ToJson();
        if (!json.ContainsKey(name) || json[name] is null) return true;
        if (!args.TryGet<int>(name, out value)) { error = $"{name} must be an integer."; value = fallback; return false; }
        if (value < 1 || value > maximum) { error = $"{name} must be between 1 and {maximum}."; return false; }
        return true;
    }
}

/// <summary>Normalizes evidence into stable newest first JSON. A source gap or truncation can never report complete.</summary>
public static class SystemMaintenanceFormatting
{
    private const string Timestamp = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    public static string Updates(MaintenanceSnapshot<UpdateRecord> snapshot, int limit) { ArgumentNullException.ThrowIfNull(snapshot); ValidateLimit(limit, SystemMaintenanceLimits.MaximumUpdates); return Format(snapshot, limit, x => x.Name, x => x.Source, x => x.PublishedUtc, x => new JsonObject { ["id"] = B(x.Id, 256), ["name"] = B(x.Name, SystemMaintenanceLimits.NameCharacters), ["currentVersion"] = B(x.CurrentVersion, 256), ["availableVersion"] = B(x.AvailableVersion, 256), ["kind"] = NormalizeKind(x.Kind), ["rebootMayBeRequired"] = x.RebootMayBeRequired, ["source"] = B(x.Source, SystemMaintenanceLimits.SourceCharacters), ["publishedUtc"] = Dt(x.PublishedUtc) }, snapshot.CatalogAge); }
    public static string History(MaintenanceSnapshot<UpdateHistoryRecord> snapshot, int limit) { ArgumentNullException.ThrowIfNull(snapshot); ValidateLimit(limit, SystemMaintenanceLimits.MaximumHistory); return Format(snapshot, limit, x => x.Name, x => x.Source, x => x.TimestampUtc, x => new JsonObject { ["timestampUtc"] = Dt(x.TimestampUtc), ["id"] = B(x.Id, 256), ["name"] = B(x.Name, SystemMaintenanceLimits.NameCharacters), ["version"] = B(x.Version, 256), ["result"] = NormalizeResult(x.Result), ["source"] = B(x.Source, SystemMaintenanceLimits.SourceCharacters) }); }
    public static string Crashes(MaintenanceSnapshot<CrashRecord> snapshot, int limit) { ArgumentNullException.ThrowIfNull(snapshot); ValidateLimit(limit, SystemMaintenanceLimits.MaximumCrashes); return Format(snapshot, limit, x => x.Process ?? "", x => x.Source, x => x.TimestampUtc, x => new JsonObject { ["timestampUtc"] = Dt(x.TimestampUtc), ["process"] = B(x.Process, 512), ["pid"] = x.Pid, ["kind"] = B(x.Kind, 128), ["dumpPath"] = B(x.DumpPath, SystemMaintenanceLimits.PathCharacters), ["eventIdOrCrashId"] = B(x.EventIdOrCrashId, 256), ["summary"] = B(x.Summary, SystemMaintenanceLimits.TextCharacters), ["source"] = B(x.Source, SystemMaintenanceLimits.SourceCharacters) }); }
    public static string Drivers(MaintenanceSnapshot<DriverRecord> snapshot, int limit) { ArgumentNullException.ThrowIfNull(snapshot); ValidateLimit(limit, SystemMaintenanceLimits.MaximumDrivers); return Format(snapshot, limit, x => x.Name, x => x.Source, _ => null, x => new JsonObject { ["name"] = B(x.Name, 512), ["pathOrModule"] = B(x.PathOrModule, SystemMaintenanceLimits.PathCharacters), ["version"] = B(x.Version, 256), ["vendor"] = B(x.Vendor, 256), ["state"] = B(x.State, 128), ["loaded"] = x.Loaded, ["addressOrSize"] = B(x.AddressOrSize, 256), ["source"] = B(x.Source, SystemMaintenanceLimits.SourceCharacters) }); }

    private static string Format<T>(MaintenanceSnapshot<T> snapshot, int limit, Func<T, string> name, Func<T, string> source, Func<T, DateTimeOffset?> timestamp, Func<T, JsonObject> create, TimeSpan? catalogAge = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ordered = snapshot.Items.OrderByDescending(timestamp).ThenBy(name, StringComparer.OrdinalIgnoreCase).ThenBy(name, StringComparer.Ordinal).ThenBy(source, StringComparer.OrdinalIgnoreCase).ThenBy(source, StringComparer.Ordinal).ToArray();
        var selected = ordered.Take(limit).Select(create).ToList();
        var truncated = snapshot.CollectionTruncated || selected.Count < ordered.Length;
        var applicable = snapshot.Sources.Where(s => s.Status != InventorySourceStatus.NotApplicable).ToArray();
        var status = applicable.Length == 0 || applicable.All(s => s.Status is InventorySourceStatus.Unavailable or InventorySourceStatus.Unsupported) ? "unavailable" : applicable.All(s => s.Status == InventorySourceStatus.Available) ? "complete" : "partial";
        var complete = status == "complete" && !truncated;
        var warnings = (snapshot.Warnings ?? []).Select(w => B(w, SystemMaintenanceLimits.WarningCharacters)).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).Take(SystemMaintenanceLimits.MaximumWarnings).ToArray();
        var root = new JsonObject { ["schemaVersion"] = 1, ["status"] = status, ["complete"] = complete, ["truncated"] = truncated, ["observedItems"] = ordered.Length, ["returnedItems"] = selected.Count, ["catalogAge"] = catalogAge?.TotalSeconds, ["sources"] = new JsonArray(snapshot.Sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.Ordinal).Select(s => (JsonNode)new JsonObject { ["name"] = B(s.Name, SystemMaintenanceLimits.SourceCharacters), ["status"] = SystemInventoryFormatting.ToWireValue(s.Status), ["detail"] = B(s.Detail, SystemMaintenanceLimits.WarningCharacters) }).ToArray()), ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()), ["items"] = new JsonArray(selected.Select(x => (JsonNode)x).ToArray()) };
        return root.ToJsonString();
    }
    private static void ValidateLimit(int limit, int maximum)
    {
        if (limit < 1 || limit > maximum) throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be between 1 and {maximum}.");
    }
    private static string? NormalizeKind(string? kind) => kind is null ? null : SystemMaintenanceArguments.UpdateKinds.Contains(kind, StringComparer.Ordinal) ? kind : "unknown";
    private static string NormalizeResult(string result) => result?.ToLowerInvariant() switch { "success" => "success", "failure" => "failure", _ => "unknown" };
    private static string? Dt(DateTimeOffset? value) => value?.ToUniversalTime().ToString(Timestamp, CultureInfo.InvariantCulture);
    private static string? B(string? value, int maximum) => value is null || value.Length <= maximum ? value : value[..maximum];
}
