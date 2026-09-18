// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace Acme.SamplePlugin;

/// <summary>
/// A harmless Read-risk tool that echoes its input back, uppercased — the whole point is to be
/// trivial to verify, not useful. Demonstrates the minimum a real tool needs: a manifest, and an
/// <see cref="ExecuteAsync"/> that never throws for an expected outcome (agentic/02-coding-
/// standards.md's error model — a tool that throws is a bug in the tool).
/// </summary>
public sealed class SampleEchoTool : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "sample.echo",
        Description = "Echoes the given message back, uppercased. Demonstrates the plugin contract; has no real effect.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("message", ToolParameterType.String, "The text to echo back.")],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var message = arguments.GetRequired<string>("message");
        return Task.FromResult(ToolCallResult.Success(message.ToUpperInvariant()));
    }
}
