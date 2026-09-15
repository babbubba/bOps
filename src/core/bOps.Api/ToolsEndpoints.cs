using bOps.Abstractions;

namespace bOps.Api;

/// <summary>Maps <c>/api/tools</c> — what an operator could ask the agent for right now (ADR-0018).</summary>
internal static class ToolsEndpoints
{
    internal static void MapToolsEndpoints(this WebApplication app) =>
        app.MapGet("/api/tools", (IToolRegistry registry) => Results.Ok(registry.GetAvailableManifests()));
}
