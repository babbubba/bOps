// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace bOps.Packages.Filesystem;

internal static class FsCopyVerificationOutput
{
    public static string Format(bool matching, long? sourceLength, long? destinationLength) =>
        JsonSerializer.Serialize(new { matching, sourceLength, destinationLength });

    public static bool IsMatching(string? output)
    {
        if (output is null) return false;
        try
        {
            using var json = JsonDocument.Parse(output);
            return json.RootElement.TryGetProperty("matching", out var matching) && matching.GetBoolean();
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
