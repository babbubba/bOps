// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Docker.DotNet.Models;

namespace bOps.Packages.Docker;

/// <summary>What <c>docker.volume.inspect</c> reported, read back by the verification of the volume tools.</summary>
internal sealed record DockerVolumeState(bool Exists, string? Name, string? Driver);

/// <summary>
/// The JSON the volume tools produce and the reader their verification uses. The mount point and the driver options are
/// deliberately not part of it: they are host paths, and bOps needs neither (ADR-0033).
/// </summary>
internal static class DockerVolumeOutput
{
    public const int MaximumLabels = 20;
    public const int MaximumLabelCharacters = 256;

    public static string Missing(string name) =>
        new JsonObject { ["schemaVersion"] = 1, ["exists"] = false, ["name"] = DockerFailure.Bound(name) }.ToJsonString();

    public static string Present(VolumeResponse volume)
    {
        var json = Describe(volume);
        json["schemaVersion"] = 1;
        json["exists"] = true;
        return json.ToJsonString();
    }

    /// <summary>One volume as a JSON object, without the schema envelope.</summary>
    public static JsonObject Describe(VolumeResponse volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var labels = new JsonObject();
        foreach (var label in (volume.Labels ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(MaximumLabels))
        {
            labels[Bound(label.Key)] = Bound(label.Value);
        }

        return new JsonObject
        {
            ["name"] = Bound(volume.Name),
            ["driver"] = Bound(volume.Driver),
            ["scope"] = Bound(volume.Scope),
            ["createdAt"] = Bound(volume.CreatedAt),
            ["labels"] = labels,
            ["labelsTruncated"] = (volume.Labels?.Count ?? 0) > MaximumLabels,
            ["optionCount"] = volume.Options?.Count ?? 0,
            ["usageBytes"] = volume.UsageData is { Size: >= 0 } usage ? usage.Size : null,
            ["referenceCount"] = volume.UsageData is { RefCount: >= 0 } references ? references.RefCount : null,
        };
    }

    /// <summary>The list result, newest budget first: as many volumes as fit the limit and the byte budget, in name order.</summary>
    public static string List(IReadOnlyList<VolumeResponse> volumes, int limit, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        var ordered = volumes.OrderBy(volume => volume.Name, StringComparer.Ordinal).ToArray();
        var selected = ordered.Take(limit).Select(Describe).ToList();
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["observedVolumes"] = ordered.Length,
            ["returnedVolumes"] = selected.Count,
            ["truncated"] = selected.Count < ordered.Length,
            ["volumes"] = new JsonArray(selected.Select(item => (JsonNode)item).ToArray()),
        };

        var output = root.ToJsonString();
        var array = root["volumes"]!.AsArray();
        while (Encoding.UTF8.GetByteCount(output) > maximumBytes && array.Count > 0)
        {
            array.RemoveAt(array.Count - 1);
            root["returnedVolumes"] = array.Count;
            root["truncated"] = true;
            output = root.ToJsonString();
        }

        return output;
    }

    /// <summary>The state an inspect output describes; <c>null</c> when it is not that shape.</summary>
    public static DockerVolumeState? TryRead(string? output)
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

            return exists.GetValue<bool>()
                ? new DockerVolumeState(true, json["name"]?.GetValue<string>(), json["driver"]?.GetValue<string>())
                : new DockerVolumeState(false, null, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Turns a verification result into a verdict, as <see cref="DockerImageOutput.Verify"/> does for images.</summary>
    public static VerificationOutcome Verify(ToolCallResult verificationResult, string action, Func<DockerVolumeState, string?> refutation)
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
            return new VerificationOutcome(VerificationStatus.Inconclusive, "docker.volume.inspect's output could not be read.");
        }

        return refutation(state) is { } reason
            ? new VerificationOutcome(VerificationStatus.Refuted, reason)
            : new VerificationOutcome(VerificationStatus.Confirmed, null);
    }

    private static string Bound(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= MaximumLabelCharacters ? value : value[..MaximumLabelCharacters] + "…";
}
