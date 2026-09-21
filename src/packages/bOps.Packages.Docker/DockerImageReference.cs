// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Packages.Docker;

/// <summary>
/// A validated image reference: either a name (<c>repository[:tag][@sha256:digest]</c>, with an optional registry host) or an
/// image id. Parsing is strict and happens before the daemon sees anything, so a value can never carry more than a reference
/// (ADR-0033). Familiar names are normalized the way Docker prints them, so a reference and what the daemon reports back for it
/// compare equal.
/// </summary>
public sealed partial class DockerImageReference
{
    private const int MaximumLength = 255;

    private DockerImageReference(string original, string? repository, string? tag, string? digest, string? id)
    {
        Original = original;
        Repository = repository;
        Tag = tag;
        Digest = digest;
        Id = id;
    }

    /// <summary>The text as supplied.</summary>
    public string Original { get; }

    /// <summary>The familiar repository (registry host included unless it is Docker Hub); <c>null</c> for an id.</summary>
    public string? Repository { get; }

    /// <summary>The tag, or <c>latest</c> when a name carries neither a tag nor a digest; <c>null</c> for an id or a digest reference.</summary>
    public string? Tag { get; }

    /// <summary>The <c>sha256:</c> digest of a digest reference.</summary>
    public string? Digest { get; }

    /// <summary>The image id (with the <c>sha256:</c> prefix removed, lower case) when the value is an id; <c>null</c> for a name.</summary>
    public string? Id { get; }

    /// <summary>True when the value is an image id rather than a name.</summary>
    public bool IsId => Id is not null;

    /// <summary>
    /// The normalized reference as the daemon reports it in <c>RepoTags</c>: <c>repository:tag</c>, <c>repository@digest</c>, or the id.
    /// </summary>
    public string Familiar => Id ?? (Digest is null ? $"{Repository}:{Tag}" : $"{Repository}@{Digest}");

    /// <summary>Parses a name or an id. On failure <paramref name="error"/> says why, in a form safe to show the model.</summary>
    public static bool TryParse(string? value, out DockerImageReference? reference, out string? error)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "An image reference must not be empty.";
            return false;
        }

        if (value.Length > MaximumLength)
        {
            error = $"An image reference is at most {MaximumLength} characters.";
            return false;
        }

        if (IdPattern().IsMatch(value))
        {
            var id = value.StartsWith("sha256:", StringComparison.Ordinal) ? value["sha256:".Length..] : value;
            reference = new DockerImageReference(value, null, null, null, id.ToLowerInvariant());
            error = null;
            return true;
        }

        return TryParseName(value, out reference, out error);
    }

    /// <summary>
    /// Parses a name only. A value that would also be a valid image id is refused, so a name can never be mistaken for one.
    /// </summary>
    public static bool TryParseName(string? value, out DockerImageReference? reference, out string? error)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "An image reference must not be empty.";
            return false;
        }

        if (value.Length > MaximumLength)
        {
            error = $"An image reference is at most {MaximumLength} characters.";
            return false;
        }

        if (IdPattern().IsMatch(value))
        {
            error = "An image name must not look like an image id.";
            return false;
        }

        var match = NamePattern().Match(value);
        if (!match.Success)
        {
            error = "Not a valid image reference: use repository[:tag][@sha256:digest], lower case, with an optional registry host.";
            return false;
        }

        var name = match.Groups["name"].Value;
        var tag = match.Groups["tag"].Success ? match.Groups["tag"].Value : null;
        var digest = match.Groups["digest"].Success ? match.Groups["digest"].Value : null;

        var slash = name.IndexOf('/', StringComparison.Ordinal);
        string? host = null;
        var path = name;
        if (slash > 0)
        {
            var first = name[..slash];
            if (first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost")
            {
                host = first;
                path = name[(slash + 1)..];
            }
        }

        if (!PathPattern().IsMatch(path))
        {
            error = "Not a valid image reference: the repository must be lower case letters, digits and . _ - separators.";
            return false;
        }

        if (host is "docker.io" or "index.docker.io" or "registry-1.docker.io")
        {
            host = null;
        }
        else if (host is not null && !HostPattern().IsMatch(host))
        {
            error = "Not a valid image reference: the registry host is malformed.";
            return false;
        }

        if (host is null && path.StartsWith("library/", StringComparison.Ordinal) && path.IndexOf('/', "library/".Length) < 0)
        {
            path = path["library/".Length..];
        }

        var repository = host is null ? path : $"{host}/{path}";
        if (digest is null && tag is null)
        {
            tag = "latest";
        }

        reference = new DockerImageReference(value, repository, tag, digest, null);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Familiar;

    [GeneratedRegex("^(?:sha256:)?[a-fA-F0-9]{12,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    // name[:tag][@sha256:digest] where name may start with a registry host[:port]. The host part is validated again below.
    [GeneratedRegex(
        @"^(?<name>[A-Za-z0-9][A-Za-z0-9._:/-]*?)(?::(?<tag>[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}))?(?:@(?<digest>sha256:[a-f0-9]{64}))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*(?::[0-9]{1,5})?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(
        "^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
