// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>Metadata minimized from 12 archived campaign states plus the two PRE-6 states; no original payloads.</summary>
public sealed class DiagnosticAssuranceArchiveTests
{
    [Fact]
    public void ArchivedEvidenceShapes_HaveZeroFalseChecked_AndSurvivePersistedStateRoundtrip()
    {
        using var stream = typeof(DiagnosticAssuranceArchiveTests).Assembly.GetManifestResourceStream(
            "bOps.Runtime.Tests.Fixtures.diagnostic-assurance-metadata.json")!;
        using var document = JsonDocument.Parse(stream);
        Assert.Equal(14, document.RootElement.GetArrayLength());
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var stateJson = item.GetProperty("state").GetRawText();
            var state = JsonSerializer.Deserialize(stateJson, DiagnosticAssuranceTestJsonContext.Default.TaskState)!;
            var expected = Enum.Parse<DiagnosticAssuranceLevel>(item.GetProperty("expectedLevel").GetString()!);
            var first = DiagnosticAssurance.From(state)!;
            Assert.True(first.Level == expected, $"{item.GetProperty("case").GetString()}: expected {expected}, got {first.Level}");
            Assert.NotEqual(DiagnosticAssuranceLevel.Checked, first.Level);
            var roundtrip = JsonSerializer.Serialize(state, DiagnosticAssuranceTestJsonContext.Default.TaskState);
            var restored = JsonSerializer.Deserialize(roundtrip, DiagnosticAssuranceTestJsonContext.Default.TaskState)!;
            var second = DiagnosticAssurance.From(restored)!;
            Assert.Equal(first.Level, second.Level);
            Assert.Equal(first.Reasons.ToArray(), second.Reasons.ToArray());
            Assert.Equal(AgentTaskStatus.Completed, state.Status);
            Assert.Equal(64, item.GetProperty("sourceSha256").GetString()!.Length);
        }
    }
}
