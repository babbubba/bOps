// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Service.Core;

/// <summary>
/// One service in a listing, with its status normalized to a small vocabulary
/// (<c>"running"</c>, <c>"stopped"</c>, <c>"failed"</c>, <c>"unknown"</c>) shared by both OS
/// packages — Windows's <c>ServiceControllerStatus</c> and systemd's <c>ActiveState</c> use
/// different vocabularies neither the manifest nor the model should have to know apart
/// (agentic/01-architecture-rules.md, rule A8; see ADR-0021).
/// </summary>
public sealed record ServiceSummary(string Name, string Status);

/// <summary>
/// A detailed, single-service observation — including a definite absence when no such service is
/// installed, reported the same way <c>fs.stat</c> reports a missing path: a successful
/// observation of a negative fact, not a failure. Named as the future verification target for
/// V0.11's second tranche (<c>service.start</c>/<c>stop</c>/<c>restart</c>), per ADR-0021.
/// </summary>
public sealed record ServiceStatusResult(string Name, bool Exists, string Status, string? Description);
