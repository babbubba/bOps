// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0023: <see cref="ExecutionPlan"/> is the immutable, hashable, authorizable artifact —
/// these tests cover its structural invariants and <see cref="ExecutionPlanHasher"/>'s
/// canonicalization, since approval binds to the hash, not to the instance.
/// </summary>
public sealed class ExecutionPlanTests
{
    private static ExecutionPlanStep Step(int index, string tool = "postgres.list_bloated_tables", string? arg = "public") =>
        new(index, tool, ToolArguments.FromJson(new JsonObject { ["schema"] = arg }), "step description");

    [Fact]
    public void Constructor_Rejects_EmptySteps()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", []));
    }

    [Fact]
    public void Constructor_Rejects_NonContiguousIndices()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", [Step(0), Step(2)]));
    }

    [Fact]
    public void Constructor_Rejects_DuplicateIndices()
    {
        Assert.Throws<ArgumentException>(() =>
            new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", [Step(0), Step(0)]));
    }

    [Fact]
    public void Constructor_Accepts_ContiguousZeroBasedIndices()
    {
        var plan = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", [Step(0), Step(1)]);

        Assert.Equal(2, plan.Steps.Count);
    }

    [Fact]
    public void ComputeHash_IsDeterministic_ForTheSameContent()
    {
        var plan1 = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", [Step(0)]);
        var plan2 = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", [Step(0)]);

        Assert.Equal(ExecutionPlanHasher.ComputeHash(plan1), ExecutionPlanHasher.ComputeHash(plan2));
    }

    [Fact]
    public void ComputeHash_Differs_WhenAStepArgumentChanges()
    {
        var plan1 = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", [Step(0, arg: "public")]);
        var plan2 = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", [Step(0, arg: "other")]);

        Assert.NotEqual(ExecutionPlanHasher.ComputeHash(plan1), ExecutionPlanHasher.ComputeHash(plan2));
    }

    [Fact]
    public void ComputeHash_Differs_WhenTheRationaleChanges()
    {
        var plan1 = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Original rationale.", [Step(0)]);
        var plan2 = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Changed rationale.", [Step(0)]);

        Assert.NotEqual(ExecutionPlanHasher.ComputeHash(plan1), ExecutionPlanHasher.ComputeHash(plan2));
    }

    [Fact]
    public void ComputeHash_IsInsensitiveToJsonPropertyInsertionOrder()
    {
        // The whole point of canonicalization: two ExecutionPlanSteps whose ToolArguments were
        // built with the same keys in a different insertion order must still hash identically —
        // otherwise the hash would depend on an implementation detail of whatever assembled the
        // arguments (a Skill's own code, a deserializer), not on the plan's actual content.
        var argumentsAthenB = new JsonObject { ["a"] = 1, ["b"] = 2 };
        var argumentsBthenA = new JsonObject { ["b"] = 2, ["a"] = 1 };

        var plan1 = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.",
            [new ExecutionPlanStep(0, "postgres.list_bloated_tables", ToolArguments.FromJson(argumentsAthenB), null)]);
        var plan2 = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.",
            [new ExecutionPlanStep(0, "postgres.list_bloated_tables", ToolArguments.FromJson(argumentsBthenA), null)]);

        Assert.Equal(ExecutionPlanHasher.ComputeHash(plan1), ExecutionPlanHasher.ComputeHash(plan2));
    }

    [Fact]
    public void ExecutionPlanApproval_BindsAHashToADecision()
    {
        var plan = new ExecutionPlan("postgres.diagnose_bloat", "1.0.0", "Diagnose bloat.", [Step(0)]);
        var hash = ExecutionPlanHasher.ComputeHash(plan);
        var approval = new ExecutionPlanApproval(hash, new ApprovalDecision(true, new ActorIdentity("os-user", "alice", "Alice"), null));

        Assert.Equal(hash, approval.PlanHash);
        Assert.True(approval.Decision.Approved);
    }
}
