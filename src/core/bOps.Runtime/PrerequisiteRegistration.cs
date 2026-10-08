// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>One registered prerequisite check: what it describes and the host-assigned package that contributed it.</summary>
/// <param name="Descriptor">The validated descriptor.</param>
/// <param name="Package">The contributing package, assigned by the host (rule A11).</param>
public sealed record PrerequisiteRegistration(PrerequisiteDescriptor Descriptor, PackageId Package);
