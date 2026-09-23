// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Service.Core;

namespace bOps.Packages.Service.Linux;

internal static class LinuxServiceConfiguration
{
    internal const string Properties =
        "Id,Description,FragmentPath,ExecStart,User,Group,WorkingDirectory,UnitFileState,Restart," +
        "Requires,Wants,RequiredBy,WantedBy,After,Before,LoadState";

    internal static IReadOnlyList<string> ShowCommand(string name) => ["show", $"--property={Properties}", "--no-pager", "--", name];
    internal static IReadOnlyList<string> EnableCommand(string name) => ["enable", "--", name];
    internal static IReadOnlyList<string> DisableCommand(string name) => ["disable", "--", name];

    internal static async Task<(ServiceConfigResult Result, bool ConfirmedNotFound)> ReadAsync(string name, CancellationToken ct)
    {
        var output = await SystemctlInvoker.RunDetailedAsync(
            ShowCommand(name), ct);
        var values = SystemctlProperties.Parse(output.StandardOutput);

        if (output.ExitCode != 0)
        {
            if (IsNotFound(output.StandardError, values))
            {
                return (Empty(name, complete: true), true);
            }

            throw new InvalidOperationException($"systemctl exited with code {output.ExitCode}: {output.StandardError}");
        }

        var loadState = values.Get("LoadState");
        if (string.Equals(loadState, "not-found", StringComparison.Ordinal))
        {
            return (Empty(name, complete: true), true);
        }

        var required = Properties.Split(',');
        var complete = required.All(values.Contains);
        var state = values.Get("UnitFileState");
        var normalized = NormalizeUnitFileState(state);
        var exec = ExecStartParser.Parse(values.Get("ExecStart"));
        complete &= exec.Complete;

        return (new ServiceConfigResult(
            Exists: true,
            Name: values.Get("Id") ?? name,
            DisplayName: NullIfEmpty(values.Get("Description")),
            Description: NullIfEmpty(values.Get("Description")),
            Executable: exec.Executable,
            Arguments: exec.Arguments,
            WorkingDirectory: NullIfEmpty(values.Get("WorkingDirectory")),
            User: NullIfEmpty(values.Get("User")),
            StartupType: normalized.StartupType,
            Enabled: normalized.Enabled,
            EnablementState: normalized.EnablementState,
            RestartPolicy: NullIfEmpty(values.Get("Restart")),
            Dependencies: Units(values, "Requires", "Wants"),
            Dependents: Units(values, "RequiredBy", "WantedBy"),
            Source: "systemd/systemctl",
            Complete: complete), false);
    }

    internal static async Task<ToolCallResult> EnableAsync(string name, CancellationToken ct)
    {
        var preflight = await ReadAsync(name, ct);
        if (preflight.ConfirmedNotFound) return ToolCallResult.Failure($"Service '{name}' was not found.");
        if (preflight.Result.Enabled is true) return ToolCallResult.Success($"Service '{name}' is already enabled.");
        if (preflight.Result.Enabled is null || preflight.Result.EnablementState is "masked" or "masked-runtime")
            return ToolCallResult.Failure($"Service '{name}' has no safe enablement transition from state '{preflight.Result.EnablementState ?? "unknown"}'.");

        await SystemctlInvoker.RunAsync(EnableCommand(name), ct);
        return ToolCallResult.Success($"Requested enable of service '{name}'.");
    }

    internal static async Task<ToolCallResult> DisableAsync(string name, CancellationToken ct)
    {
        var preflight = await ReadAsync(name, ct);
        if (preflight.ConfirmedNotFound) return ToolCallResult.Failure($"Service '{name}' was not found.");
        if (preflight.Result.Enabled is false) return ToolCallResult.Success($"Service '{name}' is already disabled.");
        if (preflight.Result.Enabled is null)
            return ToolCallResult.Failure($"Service '{name}' has no safe disablement transition from state '{preflight.Result.EnablementState ?? "unknown"}'.");

        await SystemctlInvoker.RunAsync(DisableCommand(name), ct);
        return ToolCallResult.Success($"Requested disable of service '{name}'.");
    }

    internal static async Task<(IReadOnlyList<ServiceDependencyRelation> Relations, bool Complete, bool NotFound)> ReadRelationsAsync(string name, CancellationToken ct)
    {
        var output = await SystemctlInvoker.RunDetailedAsync(
            ShowCommand(name), ct);
        var values = SystemctlProperties.Parse(output.StandardOutput);
        if (output.ExitCode != 0 && IsNotFound(output.StandardError, values)) return ([], true, true);
        if (output.ExitCode != 0) throw new InvalidOperationException($"systemctl exited with code {output.ExitCode}: {output.StandardError}");
        if (string.Equals(values.Get("LoadState"), "not-found", StringComparison.Ordinal)) return ([], true, true);

        var complete = Properties.Split(',').All(values.Contains);
        var relations = new List<ServiceDependencyRelation>();
        AddRelations(relations, values, "Requires", "requires");
        AddRelations(relations, values, "Wants", "wants");
        AddRelations(relations, values, "RequiredBy", "requiredBy");
        AddRelations(relations, values, "WantedBy", "wantedBy");
        return (relations, complete, false);
    }

    internal static (bool? Enabled, string EnablementState, string StartupType) NormalizeUnitFileState(string? value) => value switch
    {
        "enabled" or "enabled-runtime" => (true, value, "automatic"),
        "disabled" => (false, value, "disabled"),
        "static" => (null, value, "static"),
        "indirect" => (null, value, "unknown"),
        "linked" or "linked-runtime" or "alias" => (null, value!, "unknown"),
        "generated" or "transient" => (null, value!, "unknown"),
        "masked" or "masked-runtime" => (false, value!, "disabled"),
        _ => (null, string.IsNullOrEmpty(value) ? "unknown" : value, "unknown"),
    };

    private static string[] Units(SystemctlProperties values, params string[] properties) =>
        properties.SelectMany(property => (values.Get(property) ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).OrderBy(unit => unit, StringComparer.Ordinal).ToArray();

    private static bool IsNotFound(string error, SystemctlProperties values) =>
        string.Equals(values.Get("LoadState"), "not-found", StringComparison.Ordinal) ||
        error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("not loaded", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("No such file", StringComparison.OrdinalIgnoreCase);

    private static ServiceConfigResult Empty(string name, bool complete) => new(
        false, name, null, null, null, null, null, null, "unknown", null, "unknown", null, [], [],
        "systemd/systemctl", complete);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static void AddRelations(List<ServiceDependencyRelation> relations, SystemctlProperties values, string property, string relation)
    {
        foreach (var unit in (values.Get(property) ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            relations.Add(new ServiceDependencyRelation(relation, unit, null));
    }
}

internal sealed class LinuxServiceConfigTool() : ServiceConfigToolBase("linux")
{
    protected override async Task<ServiceConfigResult> CollectAsync(string name, CancellationToken ct)
    {
        if (!ServiceUnitName.IsPlausible(name)) return new ServiceConfigResult(false, name, null, null, null, null, null, null, "unknown", null, "unknown", null, [], [], "systemd/systemctl", true);
        return (await LinuxServiceConfiguration.ReadAsync(name, ct)).Result;
    }
}

internal sealed class LinuxServiceDependenciesTool() : ServiceDependenciesToolBase("linux")
{
    protected override async Task<ServiceDependenciesResult> CollectAsync(string name, string direction, int limit, CancellationToken ct)
    {
        if (!ServiceUnitName.IsPlausible(name)) return new([], 0, false, true, "systemd/systemctl");
        var observed = await LinuxServiceConfiguration.ReadRelationsAsync(name, ct);
        if (observed.NotFound) return new([], 0, false, true, "systemd/systemctl");
        var relations = observed.Relations.Where(relation => direction switch
        {
            "requires" => relation.Relation is "requires" or "wants",
            "dependents" => relation.Relation is "requiredBy" or "wantedBy",
            _ => true,
        }).ToArray();
        return new(relations, relations.Length, false, observed.Complete, "systemd/systemctl");
    }
}

internal sealed class LinuxServiceEnableTool() : ServiceEnableToolBase("linux")
{
    protected override Task<ToolCallResult> EnableAsync(string name, CancellationToken ct) =>
        !ServiceUnitName.IsPlausible(name) ? Task.FromResult(ToolCallResult.Failure($"'{name}' is not a plausible systemd unit name.")) : LinuxServiceConfiguration.EnableAsync(name, ct);
}

internal sealed class LinuxServiceDisableTool() : ServiceDisableToolBase("linux")
{
    protected override Task<ToolCallResult> DisableAsync(string name, CancellationToken ct) =>
        !ServiceUnitName.IsPlausible(name) ? Task.FromResult(ToolCallResult.Failure($"'{name}' is not a plausible systemd unit name.")) : LinuxServiceConfiguration.DisableAsync(name, ct);
}

internal sealed class SystemctlProperties
{
    private readonly Dictionary<string, string> _values;
    private SystemctlProperties(Dictionary<string, string> values) => _values = values;
    internal static SystemctlProperties Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            var separator = line.IndexOf('=');
            if (separator > 0) values[line[..separator]] = line[(separator + 1)..];
        }
        return new(values);
    }
    internal string? Get(string key) => _values.GetValueOrDefault(key);
    internal bool Contains(string key) => _values.ContainsKey(key);
}

internal static class ExecStartParser
{
    internal static (string? Executable, string? Arguments, bool Complete) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null, false);
        var actions = value.Split("{ path=", StringSplitOptions.None).Length - 1;
        var pathStart = value.IndexOf("path=", StringComparison.Ordinal);
        var argvStart = value.IndexOf("argv[]=", StringComparison.Ordinal);
        if (actions != 1 || pathStart < 0 || argvStart < 0) return (null, null, false);
        var path = value[(pathStart + 5)..].Split([' ', ';', '}'], 2)[0];
        var argv = value[(argvStart + 7)..].Split(" ;", 2, StringSplitOptions.None)[0].Trim();
        var tokens = Tokenize(argv);
        if (tokens.Count == 0) return (null, null, false);
        return (path, tokens.Count == 1 ? null : string.Join(' ', tokens.Skip(1)), true);
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>(); var current = new System.Text.StringBuilder(); var quoted = false; var escaped = false;
        foreach (var c in value)
        {
            if (escaped) { current.Append(c); escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } continue; }
            current.Append(c);
        }
        if (escaped) current.Append('\\');
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
