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
}
