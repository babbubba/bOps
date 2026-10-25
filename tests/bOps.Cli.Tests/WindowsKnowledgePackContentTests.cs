// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using bOps.Knowledge;

namespace bOps.Cli.Tests;

/// <summary>
/// The Windows diagnostics Knowledge (docs/knowledge/examples/windows-event-triage-skills): the source of the signed pack the benchmark
/// and the pilot use. The tests pin its shape, not its prose: every skill is advertised by a description the admission policy accepts,
/// every resource is reachable from its skill, small enough to be read whole inside the per-read limit of the benchmark profile, and written
/// in the procedure shape (applicability, evidence, interpretation, justified and unjustified conclusions, hypotheses, next checks,
/// references). The content is generic: no benchmark figure, incident or answer is in it.
/// </summary>
public sealed partial class WindowsKnowledgePackContentTests
{
    /// <summary>The per-read character limit the benchmark profile configures (Knowledge:Budget:MaxCharactersPerRead).</summary>
    private const int BenchmarkMaxCharactersPerRead = 12_000;

    /// <summary>Entries the index lists on a normal request (KnowledgeIndexLimits.Default): every skill must be visible at once.</summary>
    private const int IndexEntries = 8;

    private static readonly string[] ProcedureSections =
    [
        "Conclusioni giustificate", "Conclusioni non giustificate", "Ipotesi da verificare", "Prossimi controlli", "Riferimenti",
    ];

    private static string SourceDirectory => Path.Combine(FindRepositoryRoot(), "docs", "knowledge", "examples", "windows-event-triage-skills");

    public static TheoryData<string> Skills() =>
        [.. Directory.EnumerateDirectories(SourceDirectory).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal)];

    [Fact]
    public void EverySkillIsVisibleInTheIndexAtOnce_AndTheSetCoversTheRequiredTopics()
    {
        var skills = Directory.EnumerateDirectories(SourceDirectory).Select(path => Path.GetFileName(path)!).ToArray();

        Assert.InRange(skills.Length, 1, IndexEntries);
        string[] required =
        [
            "windows-crash-classification", "windows-bugcheck-codes-core", "windows-display-bugchecks", "windows-event-log-sources",
            "windows-incident-correlation", "windows-hardware-troubleshooting", "windows-cpu-memory-and-reporting", "windows-event-triage",
        ];
        Assert.Empty(required.Except(skills));
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void ASkill_IsAdvertisedByAValidName_AndADescriptionTheAdmissionPolicyAccepts(string skill)
    {
        var (name, description) = Frontmatter(File.ReadAllText(Path.Combine(SourceDirectory, skill, "SKILL.md")));

        Assert.Equal(skill, name);
        Assert.True(KnowledgeTextPolicy.IsValidSkillName(name));
        Assert.Equal(KnowledgeTextViolation.None,
            KnowledgeTextPolicy.ValidateDescription(description, KnowledgeTextPolicy.MaxDescriptionLength, KnowledgeTextPolicy.MaxDescriptionUtf8Bytes));
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void EveryResourceIsReachableFromItsSkill_AndNothingIsListedThatDoesNotExist(string skill)
    {
        var directory = Path.Combine(SourceDirectory, skill);
        var body = File.ReadAllText(Path.Combine(directory, "SKILL.md"));
        var listed = ResourcePath().Matches(body).Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
        var present = Directory.Exists(Path.Combine(directory, "references"))
            ? Directory.EnumerateFiles(Path.Combine(directory, "references")).Select(path => "references/" + Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray()
            : [];

        Assert.Equal(present, listed);
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void EveryResourceFitsTheReadLimit_IsPlainText_AndKeepsTheProcedureShape(string skill)
    {
        var references = Path.Combine(SourceDirectory, skill, "references");
        foreach (var path in Directory.EnumerateFiles(references))
        {
            var text = File.ReadAllText(path);
            var name = Path.GetFileName(path);

            Assert.True(text.Length <= BenchmarkMaxCharactersPerRead, $"{skill}/{name} is {text.Length} characters: split it so it is read whole.");
            Assert.EndsWith(".md", name, StringComparison.Ordinal);
            if (name == "interpretation.md")
            {
                continue; // the original general procedure of the pack, written before this shape was fixed
            }

            foreach (var section in ProcedureSections)
            {
                Assert.True(text.Contains("## " + section, StringComparison.Ordinal), $"{skill}/{name} lacks the section '{section}'.");
            }

            Assert.Contains("**Quando si applica:**", text, StringComparison.Ordinal);
            Assert.Matches(@"https://learn\.microsoft\.com/\S+", text);
        }
    }

    [Fact]
    public void TheContentIsGeneric_NoBenchmarkAnswerFigureOrMachineIsInIt()
    {
        string[] forbidden = ["B01", "B02", "B03", "benchmark", "fixture", "golden", "tenant-a", "DESKTOP-", "alice"];
        foreach (var path in Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            foreach (var word in forbidden)
            {
                // interpretation.md says it carries no benchmark logs; that sentence is the one permitted mention.
                if (Path.GetFileName(path) == "interpretation.md" && word == "benchmark")
                {
                    continue;
                }

                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheCoreCodesAndTheLiveDumpDistinction_AreDocumentedWhereAModelWillLookForThem()
    {
        var display = File.ReadAllText(Path.Combine(SourceDirectory, "windows-display-bugchecks", "references", "bugcheck-0x193-video-dxgkrnl-livedump.md"));
        var codes = Directory.EnumerateFiles(Path.Combine(SourceDirectory, "windows-bugcheck-codes-core", "references")).Select(path => Path.GetFileName(path)!).ToArray();

        Assert.Contains("live dump", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non e un BSOD", File.ReadAllText(Path.Combine(SourceDirectory, "windows-crash-classification", "references", "live-dump-vs-bugcheck.md")) + display, StringComparison.Ordinal);
        foreach (var code in new[] { "0x101", "0x124", "0x50" })
        {
            Assert.Contains(codes, file => file.Contains(code, StringComparison.Ordinal));
        }

        foreach (var file in new[] { "bugcheck-0x116-video-tdr-failure.md", "tdr-and-display-driver-events.md" })
        {
            Assert.True(File.Exists(Path.Combine(SourceDirectory, "windows-display-bugchecks", "references", file)));
        }
    }

    private static (string Name, string Description) Frontmatter(string skillFile)
    {
        var match = FrontmatterBlock().Match(skillFile.ReplaceLineEndings("\n"));
        Assert.True(match.Success, "SKILL.md must start with a frontmatter block.");
        string Field(string key) => Regex.Match(match.Groups[1].Value, "^" + key + @": (.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        return (Field("name"), Field("description"));
    }

    [GeneratedRegex(@"\A---\n(.*?)\n---\n", RegexOptions.Singleline)]
    private static partial Regex FrontmatterBlock();

    [GeneratedRegex(@"`(references/[A-Za-z0-9._-]+\.md)`")]
    private static partial Regex ResourcePath();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
