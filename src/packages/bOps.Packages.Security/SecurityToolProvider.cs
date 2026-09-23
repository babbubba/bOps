// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Security;

/// <summary>Contributes narrowly scoped read-only TLS and certificate diagnostics.</summary>
public sealed class SecurityToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() => [new TlsProbeTool(), new CertificateListTool(), new CertificateInspectTool()];
}
