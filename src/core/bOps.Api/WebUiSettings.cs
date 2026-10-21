// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.FileProviders;

namespace bOps.Api;

/// <summary>
/// The validated <c>WebUi</c> configuration section (F-26, BOPS-0120): whether this host serves the compiled Angular SPA and from where.
/// <c>WebUi:Enabled</c> is tri-state. Unset means automatic: on exactly when <c>WebUi:RootPath</c> (default <c>wwwroot-ui</c>, relative to
/// the application directory, not to the working directory) exists, which only a published artifact that carries the SPA does; a
/// development build, the API test hosts and a backend-only publish stay API-only. <c>true</c> requires the directory; <c>false</c>
/// never serves it. A directory without <c>index.html</c> fails startup in both cases: an incomplete frontend is never served silently.
/// </summary>
internal sealed class WebUiSettings : IDisposable
{
    public const string SectionName = "WebUi";
    public const string DefaultRootPath = "wwwroot-ui";
    public const string IndexFileName = "index.html";

    private WebUiSettings(bool enabled, string? rootPath, PhysicalFileProvider? files)
    {
        Enabled = enabled;
        RootPath = rootPath;
        Files = files;
    }

    public bool Enabled { get; }

    /// <summary>The absolute directory that is served, and nothing else is; <c>null</c> when the UI is not hosted.</summary>
    public string? RootPath { get; }

    /// <summary>A file provider rooted at <see cref="RootPath"/> (the only files the UI hosting can reach); <c>null</c> when the UI is not hosted.</summary>
    public PhysicalFileProvider? Files { get; }

    public void Dispose() => Files?.Dispose();

    public static WebUiSettings Load(IConfiguration configuration, string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        var section = configuration.GetSection(SectionName);
        bool? requested = null;
        if (section["Enabled"] is { Length: > 0 } raw)
        {
            if (!bool.TryParse(raw, out var parsed))
            {
                throw Invalid("Enabled", "must be true or false (or unset for automatic detection)");
            }

            requested = parsed;
        }

        if (requested == false)
        {
            return new WebUiSettings(false, null, null);
        }

        var configured = section["RootPath"];
        if (configured is not null && string.IsNullOrWhiteSpace(configured))
        {
            throw Invalid("RootPath", "must not be empty");
        }

        var root = Path.GetFullPath(configured ?? DefaultRootPath, applicationDirectory);
        if (!Directory.Exists(root))
        {
            return requested == true
                ? throw Invalid("RootPath", $"'{root}' does not exist; publish the application with its UI (scripts/Publish-Bops.ps1) or set 'WebUi:Enabled' to false")
                : new WebUiSettings(false, null, null);
        }

        if (!File.Exists(Path.Combine(root, IndexFileName)))
        {
            throw Invalid("RootPath", $"'{root}' exists but holds no {IndexFileName}: the UI build is incomplete");
        }

        return new WebUiSettings(true, root, new PhysicalFileProvider(root));
    }

    private static InvalidOperationException Invalid(string key, string reason) =>
        new($"'{SectionName}:{key}' {reason}.");
}
