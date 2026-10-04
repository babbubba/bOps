// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Api;

/// <summary>
/// The validated <c>BrowserSession</c> configuration section (ADR-0043 §12). Built once at startup; an invalid value fails startup
/// with a message naming the key, never clamped. There is deliberately no key for <c>Secure</c>, <c>HttpOnly</c>,
/// <c>SameSite</c>, the <c>__Host-</c> prefix or the CSRF gate.
/// </summary>
internal sealed class BrowserSessionSettings
{
    public const string SectionName = "BrowserSession";
    public const string DefaultOrigins = "http://localhost:4200";
    public const string DefaultFilePath = "sessions.db";
    public const int MaximumOrigins = 8;

    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultAbsoluteTimeout = TimeSpan.FromHours(12);

    private static readonly TimeSpan MinimumIdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumIdleTimeout = TimeSpan.FromDays(1);
    private static readonly TimeSpan MinimumAbsoluteTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumAbsoluteTimeout = TimeSpan.FromDays(7);

    private BrowserSessionSettings(IReadOnlySet<BrowserOrigin> origins, TimeSpan idleTimeout, TimeSpan absoluteTimeout, string filePath)
    {
        Origins = origins;
        IdleTimeout = idleTimeout;
        AbsoluteTimeout = absoluteTimeout;
        FilePath = filePath;
    }

    /// <summary>The trusted UI origins, canonicalized (<see cref="BrowserOrigin"/>).</summary>
    public IReadOnlySet<BrowserOrigin> Origins { get; }

    public TimeSpan IdleTimeout { get; }

    public TimeSpan AbsoluteTimeout { get; }

    public string FilePath { get; }

    public long IdleTimeoutMs => (long)IdleTimeout.TotalMilliseconds;

    public long AbsoluteTimeoutMs => (long)AbsoluteTimeout.TotalMilliseconds;

    public static BrowserSessionSettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);

        // Scalar on purpose (like ApiKeys:*:Roles): an array would let hierarchical configuration merge indices and silently keep an
        // extra trusted origin, and reading it as a scalar would silently fall back to the default. Either way, refuse it.
        if (section.GetSection("Origins").GetChildren().Any())
        {
            throw Invalid("Origins", "must be one comma-separated string, not an array");
        }

        var origins = ParseOrigins(section["Origins"] ?? DefaultOrigins);
        var idle = ParseTimeout(section, "IdleTimeout", DefaultIdleTimeout, MinimumIdleTimeout, MaximumIdleTimeout);
        var absolute = ParseTimeout(section, "AbsoluteTimeout", DefaultAbsoluteTimeout, MinimumAbsoluteTimeout, MaximumAbsoluteTimeout);
        if (idle > absolute)
        {
            throw Invalid("IdleTimeout", "must not be longer than 'BrowserSession:AbsoluteTimeout'");
        }

        var filePath = section["FilePath"] ?? DefaultFilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw Invalid("FilePath", "must not be empty");
        }

        return new BrowserSessionSettings(origins, idle, absolute, filePath);
    }

    private static HashSet<BrowserOrigin> ParseOrigins(string value)
    {
        var entries = value.Split(',', StringSplitOptions.TrimEntries);
        if (entries.Length is 0 or > MaximumOrigins || entries.Any(string.IsNullOrEmpty))
        {
            throw Invalid("Origins", $"must list 1 to {MaximumOrigins} comma-separated origins with no empty entry");
        }

        var origins = new HashSet<BrowserOrigin>();
        foreach (var entry in entries)
        {
            if (!BrowserOrigin.TryParseOrigin(entry, out var origin))
            {
                throw Invalid("Origins", $"entry '{entry}' is not exactly scheme://host[:port]");
            }

            if (origin.Scheme == "http" && origin.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
            {
                throw Invalid("Origins", $"entry '{entry}' uses http on a host other than localhost, 127.0.0.1 or [::1]; use https");
            }

            if (!origins.Add(origin))
            {
                throw Invalid("Origins", $"entry '{entry}' duplicates another entry");
            }
        }

        return origins;
    }

    private static TimeSpan ParseTimeout(IConfigurationSection section, string key, TimeSpan fallback, TimeSpan minimum, TimeSpan maximum)
    {
        var raw = section[key];
        if (raw is null)
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value))
        {
            throw Invalid(key, "is not a valid TimeSpan");
        }

        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw Invalid(key, "must be a whole number of seconds");
        }

        if (value < minimum || value > maximum)
        {
            throw Invalid(key, $"must be between {minimum:c} and {maximum:c}");
        }

        return value;
    }

    private static InvalidOperationException Invalid(string key, string reason) =>
        new($"'{SectionName}:{key}' {reason}.");
}
