// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>The inclusive range of engine versions a pack declares itself compatible with (three numeric components, for example 1.3.0).</summary>
/// <param name="Min">The lowest compatible engine version.</param>
/// <param name="Max">The highest compatible engine version.</param>
public sealed record PackEngineRange(string Min, string Max);
