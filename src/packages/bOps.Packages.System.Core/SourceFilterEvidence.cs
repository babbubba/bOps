// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// What the host's own registry says about the name given as <c>source</c> (ADR-0061 §2.3). A statement about the name, never about
/// events; the records actually read are the separate <c>observedMatch</c> dimension.
/// </summary>
public enum SourceRegistration
{
    /// <summary>The name cannot be verified: no registry exists (Linux), or the catalog could not be enumerated or was empty.</summary>
    Unknown,

    /// <summary>The platform's source catalog was enumerated and lists the name (compared ignoring case).</summary>
    Registered,

    /// <summary>The platform's source catalog was enumerated, is not empty, and does not list the name.</summary>
    NotRegistered,
}

/// <summary>
/// The <c>sourceFilter</c> object of a <c>system.events</c> result (ADR-0061): whether the requested name is registered, whether a
/// record the collector read matched, and a deterministic caution when neither confirms the name. It never alters completeness and
/// carries no text from any record or from the host's catalog.
/// </summary>
public static class SourceFilterEvidence
{
    /// <summary>The one matching rule of the first version: whole string, ignoring case.</summary>
    public const string Matching = "exactCaseInsensitive";

    /// <summary>The caution when no record matched and the name is not registered.</summary>
    public const string CautionNotRegistered = "noMatchSourceNotRegistered";

    /// <summary>The caution when no record matched and the name cannot be verified.</summary>
    public const string CautionUnverified = "noMatchSourceUnverified";

    /// <summary>
    /// The fixed reading of the object, carried in every result that has one. Deterministic, provider-neutral and never derived from
    /// data; it names no domain or diagnostic category.
    /// </summary>
    public const string Semantics =
        "Only records matching requested in matchedFields were kept. "
        + "Zero matches does not show that nothing of that kind was logged: "
        + "no source here may use that name, and other sources and logs were not queried.";

    /// <summary>The wire name of a registration state.</summary>
    public static string ToWireValue(SourceRegistration registration) => registration switch
    {
        SourceRegistration.Unknown => "unknown",
        SourceRegistration.Registered => "registered",
        SourceRegistration.NotRegistered => "notRegistered",
        _ => throw new ArgumentOutOfRangeException(nameof(registration), registration, "Unknown source registration."),
    };

    /// <summary>
    /// The caution, a function of the two dimensions only: <c>null</c> when a record confirmed the name or the name is registered;
    /// otherwise a code for the missing confirmation. Truncation, status and coverage play no part.
    /// </summary>
    public static string? Caution(SourceRegistration registration, bool observedMatch) =>
        observedMatch
            ? null
            : registration switch
            {
                SourceRegistration.NotRegistered => CautionNotRegistered,
                SourceRegistration.Unknown => CautionUnverified,
                _ => null,
            };

    /// <summary>
    /// The object, or <c>null</c> when the call has no <c>source</c>. <paramref name="observedMatch"/> must come from the records the
    /// collector observed (before grouping, the limit and the byte budget).
    /// </summary>
    public static JsonNode? ToJson(SystemEventSnapshot snapshot, SystemEventQuery query, bool observedMatch)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Source is not { } requested)
        {
            return null;
        }

        return new JsonObject
        {
            ["requested"] = requested,
            ["matching"] = Matching,
            ["matchedFields"] = new JsonArray(snapshot.SourceMatchedFields
                .Order(StringComparer.Ordinal)
                .Select(field => (JsonNode)JsonValue.Create(field)!)
                .ToArray()),
            ["registrationStatus"] = ToWireValue(snapshot.SourceRegistration),
            ["observedMatch"] = observedMatch,
            ["caution"] = Caution(snapshot.SourceRegistration, observedMatch),
            ["semantics"] = Semantics,
        };
    }
}
