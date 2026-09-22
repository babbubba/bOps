// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docker.DotNet;

namespace bOps.Packages.Docker;

/// <summary>Turns the daemon's error answers into bounded text and recognizes the ones that are outcomes, not faults (ADR-0033).</summary>
internal static class DockerFailure
{
    private const int MaximumCharacters = 300;

    public static bool IsNotFound(DockerApiException exception) => exception.StatusCode == HttpStatusCode.NotFound;

    /// <summary>
    /// A daemon that cannot be reached: not running, the endpoint gone, a permission problem on the socket or a connection that
    /// timed out. A failed result, never an uncaught exception. Cancellation is not one of these and propagates.
    /// </summary>
    public static bool IsUnreachable(Exception exception) => exception is HttpRequestException or IOException or TimeoutException;

    /// <summary>The text of a failed result for a daemon that cannot be reached, bounded.</summary>
    public static string Unreachable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Bound($"The Docker daemon could not be reached: {exception.Message}");
    }

    /// <summary>The daemon's own message (from its JSON body when there is one), prefixed with the HTTP status, bounded.</summary>
    public static string Describe(DockerApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.Message;
        try
        {
            if (!string.IsNullOrWhiteSpace(exception.ResponseBody)
                && JsonNode.Parse(exception.ResponseBody) is JsonObject body
                && body["message"] is JsonNode text
                && text.GetValueKind() == JsonValueKind.String)
            {
                message = text.GetValue<string>();
            }
        }
        catch (JsonException)
        {
            // Not JSON: the exception's own text stands.
        }

        return Bound($"{(int)exception.StatusCode} {exception.StatusCode}: {message}");
    }

    /// <summary>One line, at most <see cref="MaximumCharacters"/> characters.</summary>
    public static string Bound(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaximumCharacters ? flat : flat[..MaximumCharacters] + "…";
    }
}
