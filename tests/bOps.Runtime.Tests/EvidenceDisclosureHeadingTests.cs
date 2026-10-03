// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0042 §6 and §17 rows 20 and 30: the deterministic, line-based detector of the <c>Evidence limitations</c> heading. It
/// accepts the documented heading forms in any language of body text, and rejects prose, inline code, fenced code (also
/// unclosed) and indented code.
/// </summary>
public sealed class EvidenceDisclosureHeadingTests
{
    [Theory]
    [InlineData("Evidence limitations")]
    [InlineData("Evidence limitations:")]
    [InlineData("# Evidence limitations")]
    [InlineData("## Evidence limitations")]
    [InlineData("### Evidence limitations")]
    [InlineData("#### Evidence limitations")]
    [InlineData("##### Evidence limitations")]
    [InlineData("###### Evidence limitations")]
    [InlineData("##\tEvidence limitations")]
    [InlineData("## Evidence limitations:")]
    [InlineData("**Evidence limitations**")]
    [InlineData("**Evidence limitations:**")]
    [InlineData("**Evidence limitations**:")]
    [InlineData("__Evidence limitations__")]
    [InlineData("__Evidence limitations:__")]
    [InlineData("EVIDENCE LIMITATIONS")]
    [InlineData("evidence Limitations")]
    [InlineData("   Evidence limitations")]
    [InlineData("Evidence limitations   ")]
    [InlineData("## **Evidence limitations**")]
    public void HasHeading_AcceptsTheDocumentedForms(string line)
    {
        Assert.True(EvidenceDisclosure.HasHeading($"Intro paragraph.\n\n{line}\nSomething was cut."));
        Assert.True(EvidenceDisclosure.HasHeading(line));
    }

    [Theory]
    [InlineData("There are no Evidence limitations")]
    [InlineData("`Evidence limitations`")]
    [InlineData("Evidence limitations extra")]
    [InlineData("Evidence limitations #")]
    [InlineData("## Evidence limitations ##")]
    [InlineData("####### Evidence limitations")]
    [InlineData("#Evidence limitations")]
    [InlineData("**Evidence limitations")]
    [InlineData("Evidence limitations**")]
    [InlineData("**Evidence limitations__")]
    [InlineData("Evidence  limitations")]
    [InlineData("Evidence limitation")]
    [InlineData("Evidence limitations.")]
    [InlineData("- Evidence limitations")]
    [InlineData("> Evidence limitations")]
    [InlineData("See the Evidence limitations below.")]
    [InlineData("")]
    [InlineData("****")]
    public void HasHeading_RejectsProseInlineCodeAndMalformedForms(string line)
    {
        Assert.False(EvidenceDisclosure.HasHeading($"Intro paragraph.\n{line}\nOutro."));
    }

    [Fact]
    public void HasHeading_IsFalse_ForNullAndEmpty()
    {
        Assert.False(EvidenceDisclosure.HasHeading(null));
        Assert.False(EvidenceDisclosure.HasHeading(string.Empty));
        Assert.False(EvidenceDisclosure.HasHeading("   \n  \n"));
    }

    [Fact]
    public void HasHeading_AcceptsAnItalianAnswerWithTheEnglishHeading()
    {
        const string answer = "Il computer sembra in salute.\n\n## Evidence limitations\nLa lettura degli eventi è parziale: sono stati esaminati solo gli ultimi 7 giorni.";

        Assert.True(EvidenceDisclosure.HasHeading(answer));
    }

    [Fact]
    public void HasHeading_RejectsALocalizedHeading_BecauseTheHeadingStaysEnglish()
    {
        Assert.False(EvidenceDisclosure.HasHeading("Risposta.\n\n## Limiti delle evidenze\nTesto."));
    }

    [Fact]
    public void HasHeading_AcceptsWindowsLineEndings()
    {
        Assert.True(EvidenceDisclosure.HasHeading("Answer.\r\n\r\n## Evidence limitations\r\nText.\r\n"));
        Assert.True(EvidenceDisclosure.HasHeading("Answer.\r\n**Evidence limitations**\r\n"));
    }

    [Theory]
    [InlineData("```", "```")]
    [InlineData("```text", "```")]
    [InlineData("````", "````")]
    [InlineData("~~~", "~~~")]
    [InlineData("~~~md", "~~~~")]
    [InlineData("   ```", "   ```")]
    public void HasHeading_IgnoresAHeadingInsideAFencedBlock(string open, string close)
    {
        Assert.False(EvidenceDisclosure.HasHeading($"Answer.\n{open}\n## Evidence limitations\n{close}\nEnd."));
    }

    [Fact]
    public void HasHeading_FindsAHeadingAfterAClosedFence()
    {
        Assert.True(EvidenceDisclosure.HasHeading("```\ncode\n```\n## Evidence limitations\nText."));
        Assert.True(EvidenceDisclosure.HasHeading("~~~\ncode\n~~~\nEvidence limitations\nText."));
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~")]
    [InlineData("```python")]
    public void HasHeading_IgnoresAHeadingAfterAnUnclosedFence_BecauseTheFenceRunsToTheEnd(string open)
    {
        Assert.False(EvidenceDisclosure.HasHeading($"Answer.\n{open}\nsome code\n## Evidence limitations\nText."));
    }

    [Fact]
    public void HasHeading_KeepsAFenceOpen_UntilALongEnoughSameCharacterCloser()
    {
        // A shorter closer, a different character and a closer followed by text do not close the fence.
        Assert.False(EvidenceDisclosure.HasHeading("````\n```\n## Evidence limitations\n````"));
        Assert.False(EvidenceDisclosure.HasHeading("```\n~~~\n## Evidence limitations\n```"));
        Assert.False(EvidenceDisclosure.HasHeading("```\n``` text\n## Evidence limitations\n```"));
        Assert.True(EvidenceDisclosure.HasHeading("```\ncode\n```   \n## Evidence limitations"));
        Assert.True(EvidenceDisclosure.HasHeading("```\ncode\n`````\n## Evidence limitations"));
    }

    [Fact]
    public void HasHeading_DoesNotTreatAnIndentedFenceMarkerAsAFence()
    {
        // Four spaces make an indented code line, not a fence, so the heading after it is a candidate.
        Assert.True(EvidenceDisclosure.HasHeading("Answer.\n    ```\n## Evidence limitations\nText."));
    }

    [Theory]
    [InlineData("\tEvidence limitations")]
    [InlineData("\t## Evidence limitations")]
    [InlineData("    Evidence limitations")]
    [InlineData("    ## Evidence limitations")]
    [InlineData("      **Evidence limitations**")]
    public void HasHeading_IgnoresAnIndentedCodeBlockLine(string line)
    {
        Assert.False(EvidenceDisclosure.HasHeading($"Answer.\n\n{line}\n\nEnd."));
    }

    [Fact]
    public void HasHeading_AcceptsUpToThreeLeadingSpaces()
    {
        Assert.True(EvidenceDisclosure.HasHeading("Answer.\n   ## Evidence limitations"));
        Assert.False(EvidenceDisclosure.HasHeading("Answer.\n    ## Evidence limitations"));
    }

    [Theory]
    [InlineData("  ## Evidence limitations  ", "Evidence limitations")]
    [InlineData("**Evidence limitations:**", "Evidence limitations")]
    [InlineData("__Evidence limitations__:", "Evidence limitations")]
    [InlineData("### **Evidence limitations**:", "Evidence limitations")]
    [InlineData("## Evidence limitations ##", "Evidence limitations ##")]
    [InlineData("#######  x", "#######  x")]
    public void NormalizeHeadingLine_AppliesTheDocumentedStepsInOrder(string line, string expected)
    {
        Assert.Equal(expected, EvidenceDisclosure.NormalizeHeadingLine(line));
    }

    [Fact]
    public void Instruction_IsTheFixedVersionedRestatementRequest()
    {
        Assert.StartsWith("EvidenceDisclosure/v1:", EvidenceDisclosure.Instruction, StringComparison.Ordinal);
        Assert.Contains("Restate your final answer", EvidenceDisclosure.Instruction, StringComparison.Ordinal);
        Assert.Contains("Evidence limitations", EvidenceDisclosure.Instruction, StringComparison.Ordinal);
        Assert.Contains("Keep your conclusions unchanged unless the limitations require qualifying them", EvidenceDisclosure.Instruction, StringComparison.Ordinal);
        Assert.Contains("Start no new analysis", EvidenceDisclosure.Instruction, StringComparison.Ordinal);
        Assert.Contains("Do not call tools", EvidenceDisclosure.Instruction, StringComparison.Ordinal);
    }
}
