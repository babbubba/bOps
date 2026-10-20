// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>
/// The host's allow-list of pack sources (ADR-0054 G2-3). A source is admitted by object reference: the host registers an instance
/// and receives the id; trust, allowed kinds and allowed tenants are properties of that registration. An instance that is not
/// registered is refused before it is invoked, whatever it calls itself, and two instances of one type get two ids. There is no
/// default registration, no wildcard tenant and no way for a source to register or promote itself.
/// </summary>
public sealed partial class KnowledgeProviderRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<IKnowledgePackSource, Registration> byInstance = new(ReferenceEqualityComparer.Instance);
    private int sequence;

    /// <summary>Registers a source instance and returns the provider id the registry assigned.</summary>
    /// <param name="source">The instance.</param>
    /// <param name="label">A host-chosen identifier for logs and audit; the registry appends a sequence number to make the id unique.</param>
    /// <param name="trust">The trust level of this instance.</param>
    /// <param name="kinds">The pack kinds this instance may serve.</param>
    /// <param name="tenants">The tenants this instance may serve. There is no wildcard.</param>
    /// <exception cref="InvalidOperationException">The instance is already registered.</exception>
    public string Register(
        IKnowledgePackSource source,
        string label,
        KnowledgeProviderTrust trust,
        IReadOnlyCollection<PackKind> kinds,
        IReadOnlyCollection<TenantId> tenants)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(tenants);
        if (!Label().IsMatch(label))
        {
            throw new ArgumentException("A provider label is a lower-case identifier.", nameof(label));
        }

        if (kinds.Count == 0 || tenants.Count == 0)
        {
            throw new ArgumentException("A provider registration lists at least one kind and one tenant.");
        }

        lock (gate)
        {
            if (byInstance.ContainsKey(source))
            {
                throw new InvalidOperationException("This provider instance is already registered.");
            }

            var id = $"kp.{label}.{++sequence}";
            byInstance.Add(source, new Registration(id, trust, kinds.ToHashSet(), tenants.ToHashSet()));
            return id;
        }
    }

    /// <summary>Withdraws a registration. New admissions of that instance are refused; a task already holding a verified snapshot is not interrupted.</summary>
    /// <returns>Whether a registration with that id existed.</returns>
    public bool Unregister(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        lock (gate)
        {
            var match = byInstance.FirstOrDefault(pair => string.Equals(pair.Value.Id, providerId, StringComparison.Ordinal));
            return match.Key is not null && byInstance.Remove(match.Key);
        }
    }

    /// <summary>Decides whether a candidate may be used for a task scope. The candidate is matched by reference and by nothing it says.</summary>
    public ProviderAdmission Admit(IKnowledgePackSource? candidate, KnowledgeTaskScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Registration? registration = null;
        if (candidate is not null)
        {
            lock (gate)
            {
                byInstance.TryGetValue(candidate, out registration);
            }
        }

        if (registration is null)
        {
            return new ProviderAdmission(KnowledgeAdmissionCode.ProviderNotAdmitted, null, KnowledgeProviderTrust.Untrusted, new HashSet<PackKind>(), null);
        }

        var code = registration.Tenants.Contains(scope.Tenant) ? KnowledgeAdmissionCode.Admitted : KnowledgeAdmissionCode.TenantNotAllowed;
        return new ProviderAdmission(code, registration.Id, registration.Trust, registration.Kinds, code == KnowledgeAdmissionCode.Admitted ? candidate : null);
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Label();

    private sealed record Registration(string Id, KnowledgeProviderTrust Trust, HashSet<PackKind> Kinds, HashSet<TenantId> Tenants);
}
