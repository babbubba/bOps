// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace bOps.Packages.Service.Core;

/// <summary>
/// Formats <c>service.*</c> results as text for the model to read. Lives in the shared library,
/// not in either OS package, precisely because two OS packages producing <c>service.list</c> must
/// produce the same shape (agentic/01-architecture-rules.md, rule A8).
/// </summary>
public static class ServiceToolFormatting
{
    /// <summary>Formats a list of <see cref="ServiceSummary"/>.</summary>
    public static string Format(IReadOnlyList<ServiceSummary> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Count == 0)
        {
            return "No services found.";
        }

        var lines = services.Select(service => $"{service.Name}\t{service.Status}");
        return "Name\tStatus\n" + string.Join('\n', lines);
    }

    /// <summary>Formats a <see cref="ServiceStatusResult"/> as single-line JSON.</summary>
    public static string Format(ServiceStatusResult status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var json = new JsonObject
        {
            ["name"] = status.Name,
            ["exists"] = status.Exists,
            ["status"] = status.Status,
            ["description"] = status.Description,
        };
        return json.ToJsonString();
    }

    public static string Format(ServiceConfigResult config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new JsonObject
        {
        ["exists"] = config.Exists, ["name"] = config.Name, ["displayName"] = config.DisplayName,
        ["description"] = config.Description, ["executable"] = config.Executable, ["arguments"] = config.Arguments,
        ["workingDirectory"] = config.WorkingDirectory, ["user"] = config.User, ["startupType"] = config.StartupType,
        ["enabled"] = config.Enabled, ["enablementState"] = config.EnablementState, ["restartPolicy"] = config.RestartPolicy,
        ["dependencies"] = new JsonArray(config.Dependencies.Select(value => JsonValue.Create(value)).ToArray()),
        ["dependents"] = new JsonArray(config.Dependents.Select(value => JsonValue.Create(value)).ToArray()),
        ["source"] = config.Source, ["complete"] = config.Complete,
        }.ToJsonString();
    }

    public static string Format(ServiceDependenciesResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new JsonObject
        {
        ["relations"] = new JsonArray(result.Relations.Select(r => new JsonObject
        {
            ["relation"] = r.Relation, ["serviceName"] = r.ServiceName, ["status"] = r.Status,
        }).ToArray()),
        ["count"] = result.Count, ["truncated"] = result.Truncated, ["complete"] = result.Complete, ["source"] = result.Source,
        }.ToJsonString();
    }
}
