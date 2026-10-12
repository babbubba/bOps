// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>The detached signature file (<c>bops-pack.sig.json</c>): which key signed, and the signature over the manifest bytes.</summary>
/// <param name="SchemaVersion">The envelope schema version.</param>
/// <param name="Publisher">The publisher whose key signed; looked up in the trust store.</param>
/// <param name="KeyId">The key id; looked up in the trust store together with the publisher.</param>
/// <param name="Algorithm">The signature algorithm; <see cref="KnowledgePackFormat.SignatureAlgorithm"/> is the only one defined.</param>
/// <param name="Signature">The base64 RSA-PSS signature over the SHA-256 of the exact manifest bytes.</param>
public sealed record PackSignatureEnvelope(int SchemaVersion, string Publisher, string KeyId, string Algorithm, string Signature);
