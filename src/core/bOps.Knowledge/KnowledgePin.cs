// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using bOps.Abstractions;

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

    /// <summary>The task-state form of this pin: plain data in <c>bOps.Abstractions</c>, written by the runtime when the task is created.</summary>
    /// <param name="authorizedBy">The identity the host authorised the binding for.</param>
    /// <param name="snapshotId">The verified snapshot's <see cref="KnowledgeSnapshot.Id"/>.</param>
    public TaskKnowledgeBinding ToBinding(ActorIdentity authorizedBy, string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(authorizedBy);
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);
        return new TaskKnowledgeBinding(
            SchemaVersion, authorizedBy, TenantId, PackId, Version, Kind.ToString(), Publisher, KeyId, ContentHash,
            new Dictionary<string, string>(Files, StringComparer.Ordinal), snapshotId);
    }

    /// <summary>
    /// Reads a pin back from a persisted binding. The stored value is untrusted data: every identifier is shape-checked again, and a
    /// binding that is not exactly well formed is refused rather than repaired.
    /// </summary>
    /// <returns><c>false</c> when the binding is not a valid pin.</returns>
    public static bool TryFromBinding(TaskKnowledgeBinding? binding, [NotNullWhen(true)] out KnowledgePin? pin)
    {
        pin = null;
        if (binding is null
            || binding.SchemaVersion != KnowledgePackFormat.SchemaVersion
            || !Enum.TryParse<PackKind>(binding.Kind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind)
            || !PackReference.IsPackId(binding.PackId)
            || !PackReference.IsVersion(binding.Version)
            || !KnowledgeTrustStore.IsIdentifier(binding.Publisher)
            || !KnowledgeTrustStore.IsIdentifier(binding.KeyId)
            || !Knowledge.TenantId.IsValid(binding.TenantId)
            || !IsSha256(binding.ManifestHash))
        {
            return false;
        }

        var files = binding.ResourceHashes;
        if (files is null || files.Count == 0
            || files.Any(file => PackPathPolicy.Check(file.Key, KnowledgePackLimits.Default) is not null || !IsSha256(file.Value)))
        {
            return false;
        }

        pin = new KnowledgePin(
            binding.SchemaVersion, binding.TenantId, binding.PackId, binding.Version, kind, binding.Publisher, binding.KeyId,
            binding.ManifestHash,
            files.OrderBy(file => file.Key, StringComparer.Ordinal).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal));
        return true;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

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
