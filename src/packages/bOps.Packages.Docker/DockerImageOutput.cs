// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>What <c>docker.image.inspect</c> reported, read back by the verification of the image tools.</summary>
internal sealed record DockerImageState(bool Exists, string? Id, IReadOnlyList<string> Tags, IReadOnlyList<string> Digests);

/// <summary>
/// The JSON <c>docker.image.inspect</c> produces and the reader the image tools' verification uses on it. A missing image is
/// <c>exists: false</c>, so verification can tell absence from a reader that failed (ADR-0033).
/// </summary>
internal static class DockerImageOutput
{
    public const int MaximumTags = 50;
    public const int MaximumDigests = 20;

    public static string Missing(string reference) =>
        new JsonObject { ["schemaVersion"] = 1, ["exists"] = false, ["reference"] = DockerFailure.Bound(reference) }.ToJsonString();

    public static string Present(string reference, ImageInspectResponse image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["exists"] = true,
            ["reference"] = DockerFailure.Bound(reference),
            ["id"] = image.ID,
            ["tags"] = ToArray(image.RepoTags, MaximumTags),
            ["digests"] = ToArray(image.RepoDigests, MaximumDigests),
            ["sizeBytes"] = image.Size,
            ["createdUtc"] = image.Created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            ["os"] = image.Os,
            ["architecture"] = image.Architecture,
            ["variant"] = string.IsNullOrEmpty(image.Variant) ? null : image.Variant,
        }.ToJsonString();
    }

    /// <summary>The state an inspect output describes; <c>null</c> when it is not that shape (a verifier that cannot read has confirmed nothing).</summary>
    public static DockerImageState? TryRead(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(output) is not JsonObject json
                || json["exists"] is not JsonNode exists
                || exists.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (!exists.GetValue<bool>())
            {
                return new DockerImageState(false, null, [], []);
            }

            return new DockerImageState(true, json["id"]?.GetValue<string>(), Strings(json["tags"]), Strings(json["digests"]));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns a verification tool's result into a verdict: inconclusive when it did not answer or answered unreadably, otherwise
    /// refuted when <paramref name="refutation"/> gives a reason and confirmed when it gives none.
    /// </summary>
    public static VerificationOutcome Verify(ToolCallResult verificationResult, string action, Func<DockerImageState, string?> refutation)
    {
        ArgumentNullException.ThrowIfNull(verificationResult);
        ArgumentNullException.ThrowIfNull(refutation);
        if (!verificationResult.Succeeded)
        {
            return new VerificationOutcome(VerificationStatus.Inconclusive, $"Could not confirm the {action}: {verificationResult.ErrorMessage}");
        }

        var state = TryRead(verificationResult.Output);
        if (state is null)
        {
            return new VerificationOutcome(VerificationStatus.Inconclusive, "docker.image.inspect's output could not be read.");
        }

        return refutation(state) is { } reason
            ? new VerificationOutcome(VerificationStatus.Refuted, reason)
            : new VerificationOutcome(VerificationStatus.Confirmed, null);
    }

    /// <summary>A sorted, bounded JSON array of bounded strings.</summary>
    public static JsonArray ToArray(IEnumerable<string>? values, int maximum) =>
        new((values ?? [])
            .Where(value => !string.IsNullOrEmpty(value))
            .Order(StringComparer.Ordinal)
            .Take(maximum)
            .Select(value => (JsonNode)JsonValue.Create(DockerFailure.Bound(value))!)
            .ToArray());

    private static string[] Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray() : [];
}
