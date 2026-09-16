// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>
/// A plugin as tracked by <see cref="PluginStore"/>: installed, and either enabled or not
/// (rule S8 — a discovered package stays disabled until an operator enables it explicitly).
/// </summary>
/// <param name="Id">The plugin's id, as accepted from its manifest (see <see cref="PluginManifest.Id"/> and rule A11).</param>
/// <param name="InstallPath">Where this plugin's own copy of its files lives, under the plugins root.</param>
/// <param name="Manifest">The manifest snapshot as validated at install time.</param>
/// <param name="Enabled">Whether this plugin's tools are currently loaded and registered.</param>
/// <param name="InstalledAtUtc">When this plugin was installed.</param>
/// <param name="Provenance">Verification result for the exact installed bytes; <c>null</c> only for a pre-V1.0 store entry.</param>
public sealed record PluginRecord(
    string Id,
    string InstallPath,
    PluginManifest Manifest,
    bool Enabled,
    DateTimeOffset InstalledAtUtc,
    PluginProvenance? Provenance = null);
