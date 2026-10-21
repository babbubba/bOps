// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// Synthetic feasibility experiment for the structured-claim proposal in docs/benchmarks/t02-grounding-01.md.
/// This test-local oracle is NOT invoked by production, does not parse prose, and proves neither model claim extraction
/// nor agreement between claims and final prose. Scope and disjoint-partition attestations are assumed producer-authored
/// trusted inputs; the current EvidenceFact contract does not carry them. No historical export or live tool is read.
/// </summary>
public sealed class NumericClaimFeasibilityTests
{
    [Theory]
    [InlineData(14, 34, "Observed", "Observed", false, "Contradicted")]
    [InlineData(42, 43, "Observed", "Observed", false, "Contradicted")]
    [InlineData(14, 14, "Observed", "Observed", false, "Supported")]
    [InlineData(14, 14, "Observed", "LowerBound", false, "Supported")]
    [InlineData(14, 13, "LowerBound", "LowerBound", false, "Supported")]
    [InlineData(14, 34, "LowerBound", "LowerBound", false, "Unsupported")]
    [InlineData(14, 14, "Observed", "Total", false, "Unsupported")]
    [InlineData(14, 14, "Observed", "Total", true, "Unsupported")]
    [InlineData(14, 14, "LowerBound", "Total", true, "Unsupported")]
    [InlineData(14, 14, "LowerBound", "Observed", true, "Unsupported")]
    [InlineData(14, 14, "Total", "Total", true, "Supported")]
    [InlineData(14, 34, "Total", "Total", true, "Contradicted")]
    public void SingleCount_RequiresExplicitCountMeaningAndCompleteness(
        long observed, long claimed, string sourceMeaning, string claimMeaning, bool complete, string expected)
    {
        var scope = Scope(complete);
        var reference = new FactReference(scope, "count", "application-crash");
        var fact = new NumericFact(reference, observed, Enum.Parse<Meaning>(sourceMeaning));
        var claim = new NumericClaim(scope, [reference], claimed, Enum.Parse<Meaning>(claimMeaning));
        Assert.Equal(expected, Evaluate(claim, [fact]).ToString());
    }

    [Theory]
    [InlineData("task")]
    [InlineData("step")]
    [InlineData("query")]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("source")]
    [InlineData("category")]
    [InlineData("completeness")]
    [InlineData("type")]
    [InlineData("key")]
    public void DistinctProvenance_IsUnsupportedRatherThanAFalseNumericContradiction(string dimension)
    {
        var scope = Scope(false);
        var original = new FactReference(scope, "count", "application-crash");
        var other = Alter(original, dimension);
        NumericFact[] facts = [new(original, 14, Meaning.Observed), new(other, 34, Meaning.Observed)];
        Assert.Equal(Verdict.Supported, Evaluate(new(other.Scope, [other], 34, Meaning.Observed), facts));
        Assert.Equal(Verdict.Unsupported, Evaluate(new(scope, [other], 34, Meaning.Observed), [facts[0]]));
        Assert.Equal(Verdict.Supported, Evaluate(new(scope, [original], 14, Meaning.LowerBound), facts));
    }

    [Theory]
    [InlineData("attested", 42, "Supported")]
    [InlineData("attested", 43, "Contradicted")]
    [InlineData("missing", 42, "Unsupported")]
    [InlineData("overlap", 42, "Unsupported")]
    [InlineData("duplicate", 54, "Unsupported")]
    [InlineData("query", 42, "Unsupported")]
    [InlineData("from", 42, "Unsupported")]
    [InlineData("source", 42, "Unsupported")]
    [InlineData("task", 42, "Unsupported")]
    [InlineData("step", 42, "Unsupported")]
    [InlineData("total-on-partial", 42, "Unsupported")]
    public void Arithmetic_RequiresProducerAttestedDisjointPartitionAndCommonProvenance(string scenario, long claimed, string expected)
    {
        var scope = Scope(false) with { Category = "kernel" };
        var live = new FactReference(scope with { Category = "kernel-live-dump" }, "kind-count", "live");
        var bugcheck = new FactReference(scope with { Category = "kernel-bugcheck" }, "kind-count", "bugcheck");
        if (scenario is "query" or "from" or "source" or "task" or "step") bugcheck = Alter(bugcheck, scenario);
        var members = scenario == "duplicate" ? new[] { live, live } : [live, bugcheck];
        var claim = new NumericClaim(scope, members, claimed, scenario == "total-on-partial" ? Meaning.Total : Meaning.Observed);
        var attestation = scenario == "missing" ? null : new ProducerPartition(scope, members, scenario != "overlap");
        NumericFact[] facts = [new(live, 27, Meaning.Observed), new(bugcheck, 15, Meaning.Observed)];
        Assert.Equal(expected, Evaluate(claim, facts, attestation).ToString());
    }

    private static Verdict Evaluate(NumericClaim claim, NumericFact[] available, ProducerPartition? partition = null)
    {
        if (claim.Value < 0 || claim.Operands.Length == 0 || claim.Operands.Distinct().Count() != claim.Operands.Length)
            return Verdict.Unsupported;
        var sources = claim.Operands.Select(reference => available.SingleOrDefault(fact => fact.Reference == reference)).ToArray();
        if (sources.Any(fact => fact is null || fact.Value < 0)) return Verdict.Unsupported;
        if (claim.Operands.Length == 1 && claim.Scope != claim.Operands[0].Scope) return Verdict.Unsupported;
        if (claim.Operands.Length > 1 && (partition is not { Disjoint: true } || partition.Aggregate != claim.Scope
            || !partition.Members.ToHashSet().SetEquals(claim.Operands)
            || claim.Operands.Any(reference => reference.Scope with { Category = claim.Scope.Category } != claim.Scope)))
            return Verdict.Unsupported;
        var sum = sources.Sum(fact => (decimal)fact!.Value);
        if (claim.Meaning == Meaning.LowerBound) return claim.Value <= sum ? Verdict.Supported : Verdict.Unsupported;
        if (sources.Any(fact => fact!.Meaning != claim.Meaning)
            || claim.Meaning == Meaning.Total && claim.Scope.Completeness != ToolResultCompleteness.Complete)
            return Verdict.Unsupported;
        return sum == claim.Value ? Verdict.Supported : Verdict.Contradicted;
    }

    private static ClaimScope Scope(bool complete) => new("synthetic-task", 1, "query-a",
        DateTimeOffset.Parse("2026-10-20T00:00:00Z"), DateTimeOffset.Parse("2026-10-21T00:00:00Z"), "source-a", "application-crash",
        complete ? ToolResultCompleteness.Complete : ToolResultCompleteness.Partial);

    private static FactReference Alter(FactReference reference, string dimension) => dimension switch
    {
        "task" => reference with { Scope = reference.Scope with { Task = "other-task" } },
        "step" => reference with { Scope = reference.Scope with { Step = 2 } },
        "query" => reference with { Scope = reference.Scope with { Query = "query-b" } },
        "from" => reference with { Scope = reference.Scope with { From = reference.Scope.From.AddDays(-1) } },
        "to" => reference with { Scope = reference.Scope with { To = reference.Scope.To.AddDays(-1) } },
        "source" => reference with { Scope = reference.Scope with { Source = "source-b" } },
        "category" => reference with { Scope = reference.Scope with { Category = "other-category" } },
        "completeness" => reference with { Scope = reference.Scope with { Completeness = ToolResultCompleteness.Complete } },
        "type" => reference with { Type = "other-count" },
        "key" => reference with { Key = "other-key" },
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private enum Meaning { Observed, LowerBound, Total }
    private enum Verdict { Supported, Contradicted, Unsupported }
    private sealed record ClaimScope(string Task, int Step, string Query, DateTimeOffset From, DateTimeOffset To,
        string Source, string Category, ToolResultCompleteness Completeness);
    private sealed record FactReference(ClaimScope Scope, string Type, string Key);
    private sealed record NumericFact(FactReference Reference, long Value, Meaning Meaning);
    private sealed record NumericClaim(ClaimScope Scope, FactReference[] Operands, long Value, Meaning Meaning);
    private sealed record ProducerPartition(ClaimScope Aggregate, FactReference[] Members, bool Disjoint);
}
