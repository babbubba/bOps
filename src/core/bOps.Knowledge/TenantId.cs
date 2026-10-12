// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace bOps.Knowledge;

/// <summary>
/// An opaque tenant identifier, the unit of isolation for knowledge (ADR-0055). It is a shape-checked name, not a credential and not
/// the multi-tenant model of V1.4: nothing outside knowledge isolation consumes it. Values reach it from the host's tenant authority
/// (see <see cref="KnowledgeTaskScope"/>), never from a model argument or from pack metadata.
/// </summary>
public sealed partial record TenantId
{
    /// <summary>Creates a tenant id.</summary>
    /// <exception cref="ArgumentException">The value is not a lower-case identifier of at most 64 characters.</exception>
    public TenantId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Shape().IsMatch(value))
        {
            throw new ArgumentException("A tenant id is a lower-case identifier of at most 64 characters.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The single tenant of the Local Agent. It goes through the same grants and partitions as any other tenant.</summary>
    public static TenantId Local { get; } = new("local");

    /// <summary>The identifier.</summary>
    public string Value { get; }

    /// <summary>A fixed-length, path-safe key for this tenant, used to partition stores.</summary>
    internal string PartitionKey => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Value)))[..32];

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
