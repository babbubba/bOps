// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Web;

/// <summary>
/// Host configuration for <c>web.search</c> (ADR-0028). <see cref="BaseUrl"/> is operator-trusted
/// configuration — the same trust level as <c>ModelProvider:BaseUrl</c> or <c>Docker:Endpoint</c>
/// elsewhere in this host — never a value the model supplies. Empty by default: the tool is
/// unavailable (rule A8's capability-probe pattern) until an operator configures a real instance.
/// </summary>
public sealed class WebSearchOptions
{
    // Bound directly from IConfiguration ("Web:Search:BaseUrl"); empty string is the meaningful
    // "unconfigured" sentinel checked by IsConfigured, which a Uri-typed property (and the
    // ConfigurationBinder's UriTypeConverter, which rejects an empty string) would complicate.
#pragma warning disable CA1056
    public string BaseUrl { get; set; } = string.Empty;
#pragma warning restore CA1056

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxResults { get; set; } = 10;

    public int MaxResultTextBytes { get; set; } = 1024;

    public long MaxResponseBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>True only when <see cref="BaseUrl"/> is a well-formed absolute http/https URI — a local check, never a live network probe.</summary>
    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    internal void Validate()
    {
        if (!string.IsNullOrEmpty(BaseUrl) && !IsConfigured)
        {
            throw new ArgumentException("Web:Search:BaseUrl must be empty or a well-formed absolute http/https URI.", nameof(BaseUrl));
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        if (MaxResults < 1 || MaxResults > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResults));
        }

        if (MaxResultTextBytes < 64 || MaxResultTextBytes > 16 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResultTextBytes));
        }

        if (MaxResponseBytes < 1024 || MaxResponseBytes > 32 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        }
    }
}
