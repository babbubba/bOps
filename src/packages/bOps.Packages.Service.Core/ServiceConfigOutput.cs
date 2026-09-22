using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.Packages.Service.Core;

internal static class ServiceConfigOutput
{
    public static bool? TryReadEnabled(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            if (JsonNode.Parse(output) is not JsonObject json) return null;
            if (json["exists"]?.GetValueKind() != JsonValueKind.True && json["exists"]?.GetValueKind() != JsonValueKind.False) return null;
            var node = json["enabled"];
            return node?.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => null,
            };
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
