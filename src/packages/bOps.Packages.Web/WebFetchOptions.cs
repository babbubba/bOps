// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Web;

/// <summary>
/// Host configuration for <c>web.fetch</c> (ADR-0028). Every bound here is operator-owned; a
/// request cannot widen any of them. <see cref="AllowedAddresses"/>/<see cref="AllowedNetworks"/>
/// are IP literals/CIDR ranges, never hostnames — allowlisting a name instead of an address would
/// reopen the DNS-rebinding gap this package closes.
/// </summary>
public sealed class WebFetchOptions
{
    private static readonly string[] DefaultAllowedContentTypes =
    [
        "text/plain", "text/html", "text/markdown", "application/json", "application/xml", "text/xml", "text/csv",
    ];

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public int MaxRedirects { get; set; } = 5;

    public long MaxResponseBytes { get; set; } = 1024 * 1024;

    public long MaxDecompressedBytes { get; set; } = 4 * 1024 * 1024;

    public bool AllowSchemeDowngradeOnRedirect { get; set; }

    public IReadOnlyList<string> AllowedContentTypes { get; set; } = DefaultAllowedContentTypes;

    public IReadOnlyList<string> AllowedAddresses { get; set; } = [];

    public IReadOnlyList<string> AllowedNetworks { get; set; } = [];

    internal void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }

        if (OverallTimeout <= TimeSpan.Zero || OverallTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(OverallTimeout));
        }

        if (MaxRedirects < 0 || MaxRedirects > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRedirects), "Redirects are capped at a hard ceiling of 10.");
        }

        if (MaxResponseBytes < 1024 || MaxResponseBytes > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        }

        if (MaxDecompressedBytes < 1024 || MaxDecompressedBytes > 256 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDecompressedBytes));
        }

        // Constructing the policy eagerly validates every configured address/CIDR at startup
        // rather than surfacing a malformed entry only on the first request.
        _ = new IpAddressPolicy(AllowedAddresses, AllowedNetworks);
    }
}
