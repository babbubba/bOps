// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

public sealed class EvidenceFactContractTests
{
    [Fact]
    public void Validate_AcceptsMultipleBoundedTypedFacts()
    {
        var facts = new EvidenceFact[]
        {
            new("artifact.path", "primary", ToolParameterType.Path, JsonValue.Create("C:\\report.txt")!),
            new("artifact.count", "events", ToolParameterType.Integer, JsonValue.Create(3)!),
        };

        Assert.Null(EvidenceFacts.Validate(facts));
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData("type", "")]
    public void Validate_RejectsBlankIdentity(string type, string key)
    {
        var result = EvidenceFacts.Validate([new EvidenceFact(type, key, ToolParameterType.String, JsonValue.Create("value")!)]);

        Assert.Contains("must be non-empty", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsUnsupportedTypedValue()
    {
        var result = EvidenceFacts.Validate([new EvidenceFact("artifact.kind", "primary", ToolParameterType.Enum, JsonValue.Create("report")!)]);

        Assert.Equal("fact value type is not supported.", result);
    }

    [Fact]
    public void Validate_RejectsTooManyFacts()
    {
        var facts = Enumerable.Range(0, 17)
            .Select(index => new EvidenceFact("artifact.path", index.ToString(), ToolParameterType.Path, JsonValue.Create("C:\\report.txt")!))
            .ToArray();

        var result = EvidenceFacts.Validate(facts);

        Assert.Contains("at most", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsOversizedPayload()
    {
        var result = EvidenceFacts.Validate([new EvidenceFact("artifact.text", "primary", ToolParameterType.String,
            JsonValue.Create(new string('x', EvidenceFacts.MaximumValueTextLength + 1))!)]);

        Assert.Contains("invalid", result, StringComparison.Ordinal);
    }
}
