// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>The host's decision that one tenant may use one exact pack version of one kind. Visibility comes from here, never from pack metadata.</summary>
/// <param name="Tenant">The tenant that may use the pack.</param>
/// <param name="Reference">The exact pack and version.</param>
/// <param name="Kind">The kind the pack must declare in its signed manifest.</param>
public sealed record PackGrant(TenantId Tenant, PackReference Reference, PackKind Kind);
