// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace bOps.Packages.Filesystem;

/// <summary>
/// The single-line JSON shape <c>fs.hash</c> reports — structured for the same reason
/// <see cref="FsStatOutput"/> is: a future content-identity check (e.g. <c>fs.move</c> verifying
/// it moved the same bytes) reads this back reliably instead of pattern-matching a sentence.
/// </summary>
internal static class FsHashOutput
{
    public static string Format(string resolvedPath, byte[] hash, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(hash);

        var json = new JsonObject
        {
            ["path"] = resolvedPath,
            ["algorithm"] = "SHA256",
            ["hashHex"] = Convert.ToHexStringLower(hash),
            ["sizeBytes"] = sizeBytes,
        };
        return json.ToJsonString();
    }
}
