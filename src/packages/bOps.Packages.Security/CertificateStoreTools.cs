// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Security;

public sealed class CertificateListTool : ITool
{
    public ToolManifest Manifest => SecurityContracts.CertificateListManifest;
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!SecurityContracts.TryStore(arguments, out var location, out var storeName, out var error)) return Task.FromResult(ToolCallResult.Failure(error!));
        var limit = 200; int? days = null;
        if (arguments.ToJson().ContainsKey("limit") && (!arguments.TryGet<int>("limit", out limit) || limit is < 1 or > 2000)) return Task.FromResult(ToolCallResult.Failure("limit must be between 1 and 2000."));
        if (arguments.ToJson().ContainsKey("expiringWithinDays"))
        {
            if (!arguments.TryGet<int>("expiringWithinDays", out var parsed) || parsed < 0) return Task.FromResult(ToolCallResult.Failure("expiringWithinDays must be a non-negative integer."));
            days = parsed;
        }
        try
        {
            using var store = new X509Store(storeName, location); store.Open(OpenFlags.ReadOnly);
            var now = DateTime.UtcNow; var certificates = store.Certificates.Cast<X509Certificate2>().Where(c => days is null || c.NotAfter.ToUniversalTime() <= now.AddDays(days.Value)).OrderBy(c => c.Thumbprint, StringComparer.Ordinal).Take(limit).Select(SecurityContracts.Certificate).ToArray();
            return Task.FromResult(ToolCallResult.Success(new JsonObject { ["schemaVersion"] = 1, ["location"] = location.ToString(), ["store"] = storeName.ToString(), ["returnedCertificates"] = certificates.Length, ["complete"] = certificates.Length < limit, ["certificates"] = new JsonArray(certificates.Cast<JsonNode?>().ToArray()) }.ToJsonString()));
        }
        catch (CryptographicException ex) { return Task.FromResult(ToolCallResult.Failure($"Could not read certificate store: {SecurityContracts.Bound(ex.Message)}")); }
    }
}

public sealed class CertificateInspectTool : ITool
{
    public ToolManifest Manifest => SecurityContracts.CertificateInspectManifest;
    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!SecurityContracts.TryStore(arguments, out var location, out var storeName, out var error)) return Task.FromResult(ToolCallResult.Failure(error!));
        if (!arguments.TryGet<string>("thumbprint", out var requested) || string.IsNullOrWhiteSpace(requested)) return Task.FromResult(ToolCallResult.Failure("thumbprint is required."));
        var thumbprint = SecurityContracts.NormalizeThumbprint(requested);
        try
        {
            using var store = new X509Store(storeName, location); store.Open(OpenFlags.ReadOnly);
            var certificate = store.Certificates.Cast<X509Certificate2>().SingleOrDefault(c => string.Equals(SecurityContracts.NormalizeThumbprint(c.Thumbprint ?? string.Empty), thumbprint, StringComparison.Ordinal));
            if (certificate is null) return Task.FromResult(ToolCallResult.Failure($"Certificate '{thumbprint}' was not found in the requested store."));
            using (certificate)
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                var valid = chain.Build(certificate);
                return Task.FromResult(ToolCallResult.Success(new JsonObject { ["schemaVersion"] = 1, ["found"] = true, ["location"] = location.ToString(), ["store"] = storeName.ToString(), ["certificate"] = SecurityContracts.Certificate(certificate), ["chainValid"] = valid, ["chainStatuses"] = SecurityContracts.ChainStatuses(chain.ChainStatus), ["complete"] = true }.ToJsonString()));
            }
        }
        catch (CryptographicException ex) { return Task.FromResult(ToolCallResult.Failure($"Could not inspect certificate store: {SecurityContracts.Bound(ex.Message)}")); }
    }
}
