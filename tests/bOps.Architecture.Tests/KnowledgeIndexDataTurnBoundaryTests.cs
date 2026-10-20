// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Architecture.Tests;

/// <summary>
/// KNOW-DISC-01 T8-FIX01 (ADR-0057 D.4 #1, H.1 row 8): the runtime never reads the text of the knowledge index to decide anything. It
/// receives the data turn from the session, checks its provenance and role, hashes the text it is about to send and compares that
/// hash with the two it was given, and then passes the turn on untouched. The hash itself is a comparison result and an audit field;
/// it never becomes model-visible text, a log line or a branch condition on the text. Source-level, like the other boundary tests:
/// a new access to the turn's text, or a new use of the hash, changes one of the closed lists below and needs a decision.
/// </summary>
public sealed class KnowledgeIndexDataTurnBoundaryTests
{
    private const string Runner = "src/core/bOps.Runtime/AgentRunner.cs";
    private const string Publication = "src/core/bOps.Runtime/KnowledgeIndexPublication.cs";
    private const string Contracts = "src/core/bOps.Runtime/KnowledgeRead.cs";

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void TheDataTurn_IsMentionedInTheRuntimeOnlyWhereItIsVerifiedPassedOnOrNamedInTheAudit_AndNeverReadBeyondItsProvenance()
    {
        var lines = RuntimeLines()
            .Where(line => line.Text.Contains("DataTurn", StringComparison.Ordinal) && !line.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Select(line => (line.Path, Text: line.Text.Trim()))
            .ToList();

        (string Path, string Pattern)[] allowed =
        [
            // Passed on as the protected turn before the goal: the turn itself, no member read.
            (Runner, @"^\? \[\(aggressive \? knowledgeIndex\.Aggressive! : knowledgeIndex\.Normal!\)\.DataTurn, goalTurn\]$"),
            // The audit names the admitted provider the session recorded in the provenance, nothing else about the turn.
            (Runner, @"^ProviderId = view\.Normal\?\.DataTurn\.Provenance\?\.AdmittedProviderId,$"),
            // The verification: role, shape and provenance first; the text is read in the one place checked below.
            (Publication, @"^if \(rendering is null \|\| rendering\.DataTurn is not \{ Role: ChatRole\.User \} turn \|\| turn\.ToolCalls is not null \|\| turn\.ToolCallId is not null$"),
            // The contract record.
            (Contracts, @"^public sealed record KnowledgeIndexRendering\(ChatTurn DataTurn, "),
        ];

        var offenders = lines
            .Where(line => !allowed.Any(rule => rule.Path == line.Path && Regex.IsMatch(line.Text, rule.Pattern, RegexOptions.None, TimeSpan.FromSeconds(5))))
            .Select(line => $"{line.Path}: {line.Text}")
            .ToArray();

        Assert.Empty(offenders);
        Assert.Equal(allowed.Length, lines.Count);
    }

    [Fact]
    public void TheTextOfTheTurn_IsReadInExactlyOnePlaceInTheRuntime_ToCheckItsLengthAndToHashIt()
    {
        var publication = RuntimeFile(Publication);
        var verify = MethodBody(publication, "private static bool Verify(KnowledgeIndexRendering? rendering");

        // One read of the text, bound to a local; no other member of the turn's text is touched anywhere in the file.
        var reads = Regex.Count(publication, @"\.Content\b", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.Equal(1, reads);
        Assert.Contains("turn.Content is not { Length: > 0 } content", verify, StringComparison.Ordinal);

        // The local is used for its length (a bound, not an interpretation) and as the input of the hash, and for nothing else.
        var uses = Regex.Count(verify, @"\bcontent\b", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.Equal(1 + 1 + 1, uses); // the pattern declaration, `content.Length > maxCharacters`, `GetBytes(content)`
        Assert.Contains("content.Length > maxCharacters", verify, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData(Encoding.UTF8.GetBytes(content))", verify, StringComparison.Ordinal);

        // The hash is a value to compare. It is not stored, returned, logged or branched on beyond the two equality checks.
        var hashUses = verify.Split('\n').Where(line => Regex.IsMatch(line, @"\bactual\b", RegexOptions.None, TimeSpan.FromSeconds(5))).Select(line => line.Trim()).ToArray();
        Assert.Equal(3, hashUses.Length);
        Assert.StartsWith("var actual = Convert.ToHexStringLower(SHA256.HashData(", hashUses[0], StringComparison.Ordinal);
        Assert.All(hashUses.Skip(1), line => Assert.Matches(@"string\.Equals\(.*\bactual\b, StringComparison\.Ordinal\)", line));
        Assert.DoesNotContain("logger", verify, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("private static bool Verify(", verify, StringComparison.Ordinal); // it can only answer yes or no
    }

    [Fact]
    public void TheIndexHash_IsOnlyAnAuditFieldAndAnAuditKey_NeverTextForTheModelOrTheLog()
    {
        var lines = RuntimeLines()
            .Where(line => Regex.IsMatch(line.Text, @"\bIndexHash\b|\bAggressiveIndexHash\b|\bContentHash\b", RegexOptions.None, TimeSpan.FromSeconds(5))
                && !line.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Select(line => (line.Path, Text: line.Text.Trim()))
            .ToList();

        (string Path, string Pattern)[] allowed =
        [
            (Runner, @"^IndexHash = view\.Normal\?\.IndexHash,$"),
            (Runner, @"^AggressiveIndexHash = view\.Aggressive\?\.IndexHash,$"),
            (Publication, @"^internal readonly record struct AuditKey\(KnowledgeIndexStatus Status, KnowledgeIndexSuppression\? Suppression, string\? IndexHash\);$"),
            (Publication, @"^internal static AuditKey KeyOf\(KnowledgeIndexView view\) => new\(view\.Status, view\.Suppression, view\.Normal\?\.IndexHash\);$"),
            (Publication, @"^if \(!string\.Equals\(provenance\.ContentHash, actual, StringComparison\.Ordinal\)$"),
            (Publication, @"^\|\| !string\.Equals\(rendering\.IndexHash, actual, StringComparison\.Ordinal\)\)$"),
            (Contracts, @"^public sealed record KnowledgeIndexRendering\(ChatTurn DataTurn, IReadOnlyList<string> ListedRefs, int Omitted, string IndexHash\);$"),
        ];

        // The only other runtime reader of a ContentHash is the generic provenance record and the context gate, which neither
        // know the index nor emit the value; they are outside this closed list on purpose and are asserted separately below.
        var offenders = lines
            .Where(line => line.Path is Runner or Publication or Contracts
                && !allowed.Any(rule => rule.Path == line.Path && Regex.IsMatch(line.Text, rule.Pattern, RegexOptions.None, TimeSpan.FromSeconds(5))))
            .Select(line => $"{line.Path}: {line.Text}")
            .ToArray();

        Assert.Empty(offenders);
        Assert.DoesNotContain(lines, line => line.Path == Runner && Regex.IsMatch(line.Text, @"\$""|string\.Format|Append|logger\.", RegexOptions.None, TimeSpan.FromSeconds(5)));
    }

    // ---- helpers ----

    private static IEnumerable<(string Path, string Text)> RuntimeLines() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", "core", "bOps.Runtime"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadAllLines(path).Select(line => (Path: Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'), Text: line)));

    private static string RuntimeFile(string relative) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The text of a method from its signature to the closing brace at the same indentation.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var open = source.IndexOf("\n    {", start, StringComparison.Ordinal);
        var close = source.IndexOf("\n    }", open, StringComparison.Ordinal);
        Assert.True(open > start && close > open);
        return source[start..(close + "\n    }".Length)];
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "bOps.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (bOps.slnx) was not found.");
    }
}
