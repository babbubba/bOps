// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// The shape of a plugin's <c>bops-plugin.json</c> (agentic/01-architecture-rules.md, rule A1's
/// exception for <c>bOps.Abstractions</c>: this names a concept, never a concrete package). The
/// loader (<c>bOps.PluginHost</c>, V0.10, ADR-0020) validates and activates from this; nothing
/// here is enforced by <c>bOps.Abstractions</c> itself, which stays free of any loading logic.
/// </summary>
/// <param name="SchemaVersion">The manifest format version this file was written for. The loader rejects an unsupported value rather than guessing at a newer or older shape.</param>
/// <param name="Id">The plugin's claimed identifier. The *effective* <see cref="PackageId"/> a package's tools are registered under is the loader's to grant, never the plugin's to self-assign (rule A11).</param>
/// <param name="Publisher">Who publishes the plugin. Informational; carries no trust.</param>
/// <param name="Version">The plugin's own semantic version.</param>
/// <param name="MinHostAbstractionsVersion">The lowest <c>bOps.Abstractions</c> version this plugin requires. The loader rejects installation if the running host is older.</param>
/// <param name="EntryAssembly">The plugin's main assembly file name, relative to its own installed folder.</param>
/// <param name="EntryType">The fully qualified type name the loader activates — must implement exactly one of <see cref="IToolProvider"/> or <see cref="IModelProviderPackage"/>.</param>
/// <param name="DeclaredCapabilities">What the plugin claims it may register, shown to an operator before they enable it. Informational — never enforced against what the plugin actually registers.</param>
/// <param name="Dependencies">The plugin's own third-party dependencies, for license inventory. Informational — the loader resolves the plugin's real dependencies from its own folder regardless of what is declared here (agentic/02-coding-standards.md forbids <c>Assembly.LoadFrom</c>; resolution goes through the isolated load context, not this list).</param>
/// <param name="MaxDeclaredRisk">The highest risk level the plugin claims any of its tools may reach. Informational, exactly like a package manifest's declared ceiling elsewhere (agentic/03-security-rules.md, rule S3) — the operator's own <c>policy.yaml</c> package ceiling is what is actually enforced.</param>
public sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string Publisher,
    string Version,
    string MinHostAbstractionsVersion,
    string EntryAssembly,
    string EntryType,
    IReadOnlyList<string> DeclaredCapabilities,
    IReadOnlyList<PluginDependency> Dependencies,
    RiskLevel? MaxDeclaredRisk);

/// <summary>One informational third-party dependency declared by a plugin's manifest.</summary>
/// <param name="Name">The dependency's package name.</param>
/// <param name="Version">The dependency's version.</param>
public sealed record PluginDependency(string Name, string Version);
