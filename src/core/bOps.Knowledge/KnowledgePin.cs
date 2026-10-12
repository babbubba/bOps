// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace bOps.Knowledge;

/// <summary>
/// What a task pins when it admits a pack: the tenant, the complete pack identity and the hash of every payload file. It is plain
/// data that serializes to JSON, so a resumed task can ask for exactly this content again and be refused anything else. A pin is
/// never a credential: it names content, it does not grant access to it.
/// </summary>
/// <param name="SchemaVersion">The pin schema version.</param>
/// <param name="TenantId">The tenant the pack was admitted for.</param>
/// <param name="PackId">The pack id.</param>
/// <param name="Version">The pack version.</param>
/// <param name="Kind">The kind the signed manifest declared.</param>
/// <param name="Publisher">The publisher whose trusted key verified the pack.</param>
/// <param name="KeyId">The key that verified the pack.</param>
/// <param name="ContentHash">The SHA-256 of the manifest bytes.</param>
/// <param name="Files">Every payload file path with its SHA-256, in path order.</param>
public sealed record KnowledgePin(
    int SchemaVersion,
    string TenantId,
    string PackId,
    string Version,
    PackKind Kind,
    string Publisher,
    string KeyId,
    string ContentHash,
    IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The identity the pin names.</summary>
    public PackIdentity Identity => new(PackId, Version, Kind, Publisher, KeyId, ContentHash);

    /// <summary>Serializes the pin.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, KnowledgeJsonContext.Default.KnowledgePin);

    /// <summary>Reads a pin. The text is untrusted: a malformed pin is an error, and a well-formed one only names content.</summary>
    /// <exception cref="InvalidDataException">The text is not a pin.</exception>
    public static KnowledgePin FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize(json, KnowledgeJsonContext.Default.KnowledgePin)
                ?? throw new InvalidDataException("The pin is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The pin is not valid.", ex);
        }
    }

    internal static KnowledgePin For(TenantId tenant, VerifiedKnowledgePack pack) => new(
        KnowledgePackFormat.SchemaVersion,
        tenant.Value,
        pack.Identity.PackId,
        pack.Identity.Version,
        pack.Identity.Kind,
        pack.Identity.Publisher,
        pack.Identity.KeyId,
        pack.Identity.ContentHash,
        pack.Manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal));
}
