// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

public sealed class DiagnosticAssuranceTests
{
    [Theory]
    [InlineData(AgentTaskStatus.Running)]
    [InlineData(AgentTaskStatus.Failed)]
    [InlineData(AgentTaskStatus.Cancelled)]
    [InlineData(AgentTaskStatus.PolicyBlocked)]
    public void NonCompletedTask_HasNoProjection(AgentTaskStatus status) =>
        Assert.Null(DiagnosticAssurance.From(Task(Read(), Final()) with { Status = status }));

    [Fact]
    public void CompleteTypedEvidenceWithModelCheck_IsCheckedWithoutClaimingTruth()
    {
        var result = Project(Task(Read(), Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Checked, result.Level);
        Assert.Equal([new AssuranceReason(AssuranceReasonCode.GroundingNoContradictionCited, 1)], result.Reasons);
    }

    [Theory]
    [InlineData(ToolResultCompleteness.Partial, AssuranceReasonCode.EvidencePartial)]
    [InlineData(ToolResultCompleteness.Unavailable, AssuranceReasonCode.EvidenceUnavailable)]
    public void LimitedEvidence_ModelNoContradictionCannotRemoveQualification(
        ToolResultCompleteness completeness, AssuranceReasonCode code)
    {
        var result = Project(Task(Read(completeness: completeness), Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Qualified, result.Level);
        Has(result, code);
        Has(result, AssuranceReasonCode.GroundingNoContradictionCited);
    }

    [Fact]
    public void HistoricalUnspecifiedCompleteness_CannotBeChecked()
    {
        var result = Project(Task(Read(completeness: ToolResultCompleteness.Unspecified), Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Unchecked, result.Level);
        Has(result, AssuranceReasonCode.EvidenceCompletenessUnspecified);
    }

    [Fact]
    public void CompleteUntypedCpuMemoryArchetype_IsUnchecked()
    {
        var result = Project(Task(Read(facts: []), Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Unchecked, result.Level);
        Has(result, AssuranceReasonCode.NoTypedFactsForStep);
    }

    [Theory]
    [InlineData((int)EvidenceGroundingOutcome.NotChecked, DiagnosticAssuranceLevel.Unchecked, AssuranceReasonCode.GroundingNotChecked)]
    [InlineData((int)EvidenceGroundingOutcome.CheckUnavailable, DiagnosticAssuranceLevel.Unchecked, AssuranceReasonCode.GroundingCheckUnavailable)]
    [InlineData((int)EvidenceGroundingOutcome.Corrected, DiagnosticAssuranceLevel.Qualified, AssuranceReasonCode.GroundingCorrected)]
    public void GroundingOutcome_HasExplicitConservativeMeaning(
        int outcome, DiagnosticAssuranceLevel level, AssuranceReasonCode reason)
    {
        var result = Project(Task(Read(), Final((EvidenceGroundingOutcome)outcome)));
        Assert.Equal(level, result.Level);
        Has(result, reason);
    }

    [Fact]
    public void UnrecognizedFinalMarker_CannotCertifyEvidence()
    {
        var result = Project(Task(Read(), Final() with { Description = "Final response; invented suffix" }));
        Assert.Equal(DiagnosticAssuranceLevel.Unchecked, result.Level);
        Has(result, AssuranceReasonCode.GroundingNotChecked);
    }

    [Fact]
    public void EarlierCheckCannotCoverEvidenceOrUnknownFinalRecordedLater()
    {
        var task = Task(Read(), Final(), Read(101), Final() with { Index = 102, Description = "unknown final" });
        var result = Project(task);
        Assert.Equal(DiagnosticAssuranceLevel.Unchecked, result.Level);
        Has(result, AssuranceReasonCode.GroundingNotChecked);
    }

    [Fact]
    public void NoOperationalTools_KnowledgeOnlyArchetypeIsNotApplicable()
    {
        var result = Project(Task(Final()));
        Assert.Equal(DiagnosticAssuranceLevel.NotApplicable, result.Level);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void PolicyDeniedOnly_IsNotApplicable()
    {
        var denied = Read() with
        {
            Description = RuntimeStepTokens.Denied,
            Result = new ToolCallResult(ToolOutcome.Denied, null, "denied") { FailureKind = ToolFailureKind.Authorization },
        };
        Assert.Equal(DiagnosticAssuranceLevel.NotApplicable, Project(Task(denied, Final())).Level);
    }

    [Fact]
    public void FailedCollectedStep_QualifiesEvenWhenCheckCitesNoContradiction()
    {
        var failure = Read(1) with { Result = ToolCallResult.Failure("failed") };
        var result = Project(Task(Read(), failure, Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Qualified, result.Level);
        Has(result, AssuranceReasonCode.PlannedStepFailed);
    }

    [Fact]
    public void InconclusivePostActionVerification_RemainsQualifiedDespiteGrounding()
    {
        var result = Project(Task(Read() with { VerificationStatus = VerificationStatus.Inconclusive }, Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Qualified, result.Level);
        Has(result, AssuranceReasonCode.VerificationInconclusive);
    }

    [Theory]
    [InlineData(VerificationStatus.Confirmed)]
    [InlineData(VerificationStatus.NotApplicable)]
    public void ConclusiveOrInapplicableVerification_DoesNotIntroduceLimitation(VerificationStatus status)
    {
        var result = Project(Task(Read() with { VerificationStatus = status }, Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Checked, result.Level);
    }

    [Fact]
    public void MissingResult_IsUnavailableAndNeverChecked()
    {
        var result = Project(Task(Read() with { Result = null }, Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Qualified, result.Level);
        Has(result, AssuranceReasonCode.EvidenceUnavailable);
    }

    [Fact]
    public void UnexecutedPlannedStep_IsQualified()
    {
        var task = Task(Read(), Final()) with
        {
            Plans = [Plan(0, 2)],
            Steps = [Read() with { PlanRevision = 0, PlannedStepIndex = 0, ExecutionClassification = PlannedStepExecutionClassification.Matched }, Final()],
        };
        var result = Project(task);
        Assert.Equal(DiagnosticAssuranceLevel.Qualified, result.Level);
        Has(result, AssuranceReasonCode.PlannedStepNotExecuted);
    }

    [Fact]
    public void RequiredLegacyFollowUpWithoutExpectedTool_IsStillUnexecuted()
    {
        var plan = new AgentPlan(0, "legacy plan", [new PlannedStep(0, "read", "test.read"), new PlannedStep(1, "required follow-up", null)]);
        var task = Task(Read() with { PlanRevision = 0 }, Final()) with { Plans = [plan] };
        var result = Project(task);
        Assert.Equal(DiagnosticAssuranceLevel.Qualified, result.Level);
        Has(result, AssuranceReasonCode.PlannedStepNotExecuted);
    }

    [Fact]
    public void IntentionallySkippedConditionalStep_IsNotMissingEvidence()
    {
        var plan = Plan(0, 2) with
        {
            Steps = [new PlannedStep(0, "read", "test.read"), new PlannedStep(1, "optional", "test.read")
            {
                Activation = new EvidenceFactExists(0, "missing.fact", "missing"),
            }],
        };
        var task = Task(Read(), Final()) with
        {
            Plans = [plan],
            Steps = [Read() with { PlanRevision = 0, PlannedStepIndex = 0, ExecutionClassification = PlannedStepExecutionClassification.Matched }, Final()],
        };
        Assert.Equal(DiagnosticAssuranceLevel.Checked, Project(task).Level);
    }

    [Fact]
    public void ShortenedObservation_RemainsQualifiedWithoutDurableReadCoverage()
    {
        var result = Project(Task(Read() with { Observation = "short" }, Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Qualified, result.Level);
        Has(result, AssuranceReasonCode.ObservationShortenedNotRead);
    }

    [Fact]
    public void FullOutputWithVerificationSuffix_IsNotShortened()
    {
        var result = Project(Task(Read() with { Observation = "output\nVerification: confirmed" }, Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Checked, result.Level);
    }

    [Fact]
    public void BoundedLedgerOmissions_AreUncheckedDespiteNoContradiction()
    {
        var facts = Enumerable.Range(0, 13).Select(index => Fact(index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var result = Project(Task(Read(facts: facts), Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Unchecked, result.Level);
        Has(result, AssuranceReasonCode.GroundingFactsOmitted);
    }

    [Fact]
    public void MoreStepsThanLedgerSlots_CannotBeChecked()
    {
        var task = Task([.. Enumerable.Range(0, 13).Select(index => Read(index)), Final()]);
        Assert.Equal(DiagnosticAssuranceLevel.Unchecked, Project(task).Level);
    }

    [Fact]
    public void FactIdWithHiddenLongValue_DoesNotEstablishCheckCoverage()
    {
        var fact = new EvidenceFact("opaque.fact", "long", ToolParameterType.String, JsonValue.Create(new string('a', 200))!);
        var result = Project(Task(Read(facts: [fact]), Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Unchecked, result.Level);
        Has(result, AssuranceReasonCode.GroundingFactsOmitted);
    }

    [Fact]
    public void CorrectedFinal_QualifiesEvenWithCompleteTypedEvidence()
    {
        var result = Project(Task(Read(), Final(EvidenceGroundingOutcome.Corrected)));
        Assert.Equal(DiagnosticAssuranceLevel.Qualified, result.Level);
        Has(result, AssuranceReasonCode.GroundingCorrected);
    }

    [Fact]
    public void RecoveredFinal_IsInformationalWhenEvidenceIsCompleteAndChecked()
    {
        var recovery = new PlanStep(1, EarlyFinalRecovery.StepDescription, null, null, null) { EarlyFinalRecoveryCursor = 0 };
        var result = Project(Task(Read(), recovery, Final()));
        Assert.Equal(DiagnosticAssuranceLevel.Checked, result.Level);
        Has(result, AssuranceReasonCode.FinalAfterRecovery);
    }

    [Fact]
    public void EmptyAnswerRetry_IsRecognizedAsRecovery()
    {
        var final = Final() with
        {
            ModelCalls = [Call() with { Output = new ModelOutputObservation { Class = ModelOutputClass.Empty } }, Call(), Call()],
        };
        var result = Project(Task(Read(), final));
        Has(result, AssuranceReasonCode.FinalAfterRecovery);
    }

    [Fact]
    public void EvidenceReadOrKnowledgeContinuations_AreNotRecovery()
    {
        var controlCall = Call() with { Output = new ModelOutputObservation { Class = ModelOutputClass.ToolCall, ToolCallCount = 1 } };
        var final = Final() with { ModelCalls = [controlCall, controlCall, controlCall, Call(), Call()] };
        var result = Project(Task(Read(), final));
        Assert.DoesNotContain(result.Reasons, reason => reason.Code == AssuranceReasonCode.FinalAfterRecovery);
    }

    [Fact]
    public void CountsArePerAffectedStepAndReasonsHaveStableOrder()
    {
        var result = Project(Task(Read(completeness: ToolResultCompleteness.Partial), Read(1, ToolResultCompleteness.Partial), Final()));
        Has(result, AssuranceReasonCode.EvidencePartial, 2);
        Assert.Equal(result.Reasons.OrderBy(reason => reason.Code), result.Reasons);
        Assert.All(result.Reasons, reason => Assert.True(reason.Count > 0));
    }

    [Fact]
    public void FinalTextCannotInfluenceProjectionOrMutateTask()
    {
        var task = Task(Read(), Final());
        var before = JsonSerializer.Serialize(task, DiagnosticAssuranceTestJsonContext.Default.TaskState);
        var first = Project(task);
        var changed = task with { Steps = [task.Steps[0], task.Steps[1] with { Observation = "the answer is completely different" }] };
        AssertProjectionEqual(first, Project(changed));
        Assert.Equal(before, JsonSerializer.Serialize(task, DiagnosticAssuranceTestJsonContext.Default.TaskState));
        AssertProjectionEqual(first, Project(task));
    }

    [Fact]
    public void HistoricalTaskAndResult_SourceGeneratedRoundTripsPreserveProjection()
    {
        var task = Task(Read(completeness: ToolResultCompleteness.Partial), Final(EvidenceGroundingOutcome.Corrected));
        var serialized = JsonSerializer.Serialize(task, DiagnosticAssuranceTestJsonContext.Default.TaskState);
        var restored = JsonSerializer.Deserialize(serialized, DiagnosticAssuranceTestJsonContext.Default.TaskState)!;
        var expected = Project(task);
        AssertProjectionEqual(expected, Project(restored));
        var json = JsonSerializer.Serialize(expected, DiagnosticAssuranceTestJsonContext.Default.DiagnosticAssuranceResult);
        Assert.Contains("\"Qualified\"", json, StringComparison.Ordinal);
        Assert.Contains("\"GroundingCorrected\"", json, StringComparison.Ordinal);
        AssertProjectionEqual(expected, JsonSerializer.Deserialize(json, DiagnosticAssuranceTestJsonContext.Default.DiagnosticAssuranceResult)!);
    }

    // These are minimized synthetic shapes of documented incidents, not imports of private campaign artifacts.
    [Theory]
    [InlineData("T02-C-r1", ToolResultCompleteness.Partial, (int)EvidenceGroundingOutcome.Corrected, DiagnosticAssuranceLevel.Qualified)]
    [InlineData("T02-C-r2", ToolResultCompleteness.Partial, (int)EvidenceGroundingOutcome.NoContradictionCited, DiagnosticAssuranceLevel.Qualified)]
    [InlineData("T02-C-r3", ToolResultCompleteness.Partial, (int)EvidenceGroundingOutcome.NoContradictionCited, DiagnosticAssuranceLevel.Qualified)]
    [InlineData("Qwen38-T02-r1-complete-shape", ToolResultCompleteness.Complete, (int)EvidenceGroundingOutcome.NoContradictionCited, DiagnosticAssuranceLevel.Checked)]
    [InlineData("Qwen38-T02-r2-partial-shape", ToolResultCompleteness.Partial, (int)EvidenceGroundingOutcome.NoContradictionCited, DiagnosticAssuranceLevel.Qualified)]
    [InlineData("Qwen38-T02-r3-unchecked-shape", ToolResultCompleteness.Complete, (int)EvidenceGroundingOutcome.NotChecked, DiagnosticAssuranceLevel.Unchecked)]
    [InlineData("PRE6-Qwen-token-cap", ToolResultCompleteness.Partial, (int)EvidenceGroundingOutcome.NotChecked, DiagnosticAssuranceLevel.Qualified)]
    [InlineData("PRE6-KD-no-output", ToolResultCompleteness.Unavailable, (int)EvidenceGroundingOutcome.NotChecked, DiagnosticAssuranceLevel.Qualified)]
    [InlineData("RV01-T03-partial-shape", ToolResultCompleteness.Partial, (int)EvidenceGroundingOutcome.NotChecked, DiagnosticAssuranceLevel.Qualified)]
    [InlineData("RV01-T04-unavailable-shape", ToolResultCompleteness.Unavailable, (int)EvidenceGroundingOutcome.CheckUnavailable, DiagnosticAssuranceLevel.Qualified)]
    [InlineData("RV01-T05-recovery-complete-shape", ToolResultCompleteness.Complete, (int)EvidenceGroundingOutcome.NoContradictionCited, DiagnosticAssuranceLevel.Checked)]
    [InlineData("RV01-T06-corrected-shape", ToolResultCompleteness.Complete, (int)EvidenceGroundingOutcome.Corrected, DiagnosticAssuranceLevel.Qualified)]
    public void FrozenIncidentArchetypes_PreserveExpectedLevelAfterPersistenceRoundTrip(
        string scenario, ToolResultCompleteness completeness, int grounding, DiagnosticAssuranceLevel expected)
    {
        var task = Task(Read(completeness: completeness), Final((EvidenceGroundingOutcome)grounding)) with { Goal = scenario };
        var json = JsonSerializer.Serialize(task, DiagnosticAssuranceTestJsonContext.Default.TaskState);
        var restored = JsonSerializer.Deserialize(json, DiagnosticAssuranceTestJsonContext.Default.TaskState)!;
        Assert.Equal(expected, Project(task).Level);
        AssertProjectionEqual(Project(task), Project(restored));
    }

    private static TaskState Task(params PlanStep[] steps) =>
        new(Guid.Parse("d258cb7d-57e8-4409-b4d3-3d19467aca66"), NodeId.Local, "diagnostic goal", AgentTaskStatus.Completed, steps, [], DateTimeOffset.UnixEpoch);

    private static PlanStep Read(int index = 0, ToolResultCompleteness completeness = ToolResultCompleteness.Complete, EvidenceFact[]? facts = null) =>
        new(index, "read", new ModelToolCall($"call-{index}", "test.read", ToolArguments.Empty),
            ToolCallResult.Success("output") with { Completeness = completeness, Facts = facts ?? [Fact("observed")] }, "output");

    private static EvidenceFact Fact(string key) => new("opaque.fact", key, ToolParameterType.Integer, JsonValue.Create(1)!);

    private static PlanStep Final(EvidenceGroundingOutcome outcome = EvidenceGroundingOutcome.NoContradictionCited) =>
        new(100, FinalResponse.DescriptionFor(EvidenceDisclosureOutcome.NotAttempted, outcome), null, null, "answer");

    private static AgentPlan Plan(int revision, int count) => new(revision, "plan", [.. Enumerable.Range(0, count).Select(index => new PlannedStep(index, "collect", "test.read"))])
    { SemanticContractVersion = 1 };

    private static ModelCallRecord Call() => new("test", "test", null, DateTimeOffset.UnixEpoch, 1, ModelCallOutcome.Success, null, null, null, null, null, false);

    private static DiagnosticAssuranceResult Project(TaskState task) => Assert.IsType<DiagnosticAssuranceResult>(DiagnosticAssurance.From(task));

    private static void Has(DiagnosticAssuranceResult result, AssuranceReasonCode code, int count = 1) =>
        Assert.Contains(new AssuranceReason(code, count), result.Reasons);

    private static void AssertProjectionEqual(DiagnosticAssuranceResult expected, DiagnosticAssuranceResult actual)
    {
        Assert.Equal(expected.Level, actual.Level);
        Assert.Equal(expected.Reasons, actual.Reasons);
    }
}
