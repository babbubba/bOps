// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Security;

internal static class SecurityContracts
{
    internal const int MaxText = 2048;
    internal const int MaxSans = 100;
    internal const int MaxChainStatuses = 64;
    internal const int DefaultTimeoutMs = 5000;
    internal const int MaximumTimeoutMs = 15000;

    internal static ToolManifest TlsProbeManifest { get; } = new()
    {
        Name = "network.tls_probe", Description = "Resolves a host and performs a bounded TLS handshake without sending application data. Certificate trust failures are returned as diagnostic evidence.", Risk = RiskLevel.Read, Platforms = ["windows", "linux"], Requires = [],
        Parameters = [new("host", ToolParameterType.String, "DNS host to resolve and connect to."), new("port", ToolParameterType.Integer, "TCP port (1-65535)."), new("sniHost", ToolParameterType.String, "Optional TLS SNI and certificate hostname.", Required: false), new("timeoutMilliseconds", ToolParameterType.Integer, "Probe deadline in milliseconds (1-15000, default 5000).", Required: false), new("minTls", ToolParameterType.Enum, "system, tls12, or tls13.", Required: false, AllowedValues: ["system", "tls12", "tls13"])],
    };
    internal static ToolManifest CertificateListManifest { get; } = new()
    {
        Name = "certificate.list", Description = "Lists bounded public metadata from an allowed local X.509 certificate store. It never exports certificate or key material.", Risk = RiskLevel.Read, Platforms = ["windows", "linux"], Requires = [],
        Parameters = [new("location", ToolParameterType.Enum, "currentUser or localMachine.", AllowedValues: ["currentUser", "localMachine"]), new("store", ToolParameterType.Enum, "my, root, or ca.", AllowedValues: ["my", "root", "ca"]), new("expiringWithinDays", ToolParameterType.Integer, "Optional non-negative UTC day horizon.", Required: false), new("limit", ToolParameterType.Integer, "Maximum certificates (1-2000, default 200).", Required: false)],
    };
    internal static ToolManifest CertificateInspectManifest { get; } = new()
    {
        Name = "certificate.inspect", Description = "Finds one certificate by normalized thumbprint in an allowed local store and returns public metadata and local chain evidence.", Risk = RiskLevel.Read, Platforms = ["windows", "linux"], Requires = [],
        Parameters = [new("thumbprint", ToolParameterType.String, "Certificate thumbprint; comparison ignores casing and whitespace."), new("location", ToolParameterType.Enum, "currentUser or localMachine.", AllowedValues: ["currentUser", "localMachine"]), new("store", ToolParameterType.Enum, "my, root, or ca.", AllowedValues: ["my", "root", "ca"])],
    };

    internal static bool TryStore(ToolArguments arguments, out StoreLocation location, out StoreName store, out string? error)
    {
        location = StoreLocation.CurrentUser; store = StoreName.My;
        if (!arguments.TryGet<string>("location", out var locationText) || !TryLocation(locationText, out location)) { error = "location must be currentUser or localMachine."; return false; }
        if (!arguments.TryGet<string>("store", out var storeText) || !TryStoreName(storeText, out store)) { error = "store must be my, root, or ca."; return false; }
        error = null; return true;
    }
    internal static bool TryLocation(string? value, out StoreLocation location) { location = value == "currentUser" ? StoreLocation.CurrentUser : value == "localMachine" ? StoreLocation.LocalMachine : default; return value is "currentUser" or "localMachine"; }
    internal static bool TryStoreName(string? value, out StoreName store) { store = value == "my" ? StoreName.My : value == "root" ? StoreName.Root : value == "ca" ? StoreName.CertificateAuthority : default; return value is "my" or "root" or "ca"; }
    internal static string NormalizeThumbprint(string value) => string.Concat(value.Where(static c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    internal static string? Bound(string? value) => value is null ? null : value.Length <= MaxText ? value : string.Concat(value.AsSpan(0, MaxText), "…");
    internal static JsonObject Certificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new JsonObject { ["subject"] = Bound(certificate.Subject), ["issuer"] = Bound(certificate.Issuer), ["thumbprint"] = certificate.Thumbprint, ["serialNumber"] = certificate.SerialNumber, ["notBefore"] = certificate.NotBefore.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), ["notAfter"] = certificate.NotAfter.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), ["daysRemaining"] = Math.Floor((certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays), ["dnsSans"] = new JsonArray(DnsSans(certificate).Select(static x => (JsonNode?)x).ToArray()), ["keyAlgorithm"] = Bound(certificate.PublicKey.Oid?.FriendlyName ?? certificate.PublicKey.Oid?.Value), ["keySize"] = KeySize(certificate), ["signatureAlgorithm"] = Bound(certificate.SignatureAlgorithm?.FriendlyName ?? certificate.SignatureAlgorithm?.Value), ["hasPrivateKey"] = certificate.HasPrivateKey, ["extendedKeyUsages"] = new JsonArray(ExtendedKeyUsages(certificate).Select(static x => (JsonNode?)x).ToArray()), ["keyUsages"] = new JsonArray(KeyUsages(certificate).Select(static x => (JsonNode?)x).ToArray()) };
    }
    internal static IReadOnlyList<string> DnsSans(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions["2.5.29.17"];
        if (extension is null) return [];
        try
        {
            var names = new List<string>();
            var sequence = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (sequence.HasData)
            {
                var tag = sequence.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 2)
                {
                    names.Add(Bound(sequence.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)))!);
                }
                else { sequence.ReadEncodedValue(); }
            }
            return names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxSans).ToArray();
        }
        catch (AsnContentException) { return []; }
    }
    internal static IReadOnlyList<string> ExtendedKeyUsages(X509Certificate2 certificate) => certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SelectMany(static e => e.EnhancedKeyUsages.Cast<Oid>()).Select(static oid => oid.Value ?? oid.FriendlyName ?? string.Empty).Where(static s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    internal static IReadOnlyList<string> KeyUsages(X509Certificate2 certificate) => certificate.Extensions.OfType<X509KeyUsageExtension>().Select(static e => e.KeyUsages.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static int? KeySize(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey(); if (rsa is not null) return rsa.KeySize;
        using var ecdsa = certificate.GetECDsaPublicKey(); return ecdsa?.KeySize;
    }
    internal static JsonArray ChainStatuses(X509ChainStatus[] statuses) => new(statuses.Select(static s => $"{s.Status}: {Bound(s.StatusInformation?.Trim())}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxChainStatuses).Select(static s => (JsonNode?)s).ToArray());
}
