// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>Deterministic L4 reasoning over the typed filesystem and process evidence surface.</summary>
public sealed class FileAccessScenarioTests
{
    [Fact]
    public void PermissionEvidence_IsNotALockConclusion_AndDisappearsWhenPermissionsAreCompatible()
    {
        var denied = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity is denied delete/replace access by observed ACL evidence",
            locks: "complete=true; rows=[]"));
        var compatible = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence; effective access remains platform-dependent",
            locks: "complete=true; rows=[]"));

        Assert.Contains(denied.Inferences, item => item.Contains("Permission/access evidence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(denied.Inferences, item => item.Contains("lock holder", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(compatible.Inferences, item => item.Contains("Permission/access evidence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(string.Join(" ", denied.AllReasoning), "chmod", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Join(" ", denied.AllReasoning), "chown", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealLockHolder_IsCorrelatedWithItsProcessWithoutTerminationAdvice()
    {
        var decision = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence",
            locks: "complete=true; holder pid=42 accessKind=file-descriptor",
            inspect: "pid=42 exists=true name=editor start=2026-09-24T10:00:00Z",
            tree: "pid=42 parent=7 name=editor",
            modules: "mapped module unrelated.dll"));

        Assert.Equal(["fs.stat", "fs.permissions", "fs.locks", "process.inspect", "process.tree", "process.modules"], decision.Requested);
        Assert.Contains(decision.Inferences, item => item.Contains("lock evidence identifies PID 42", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Observed, item => item.Contains("process.inspect", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(decision.Inferences, item => item.Contains("Permission/access evidence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(string.Join(" ", decision.AllReasoning), "must be killed", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(decision.Requested, item => item is "process.stop" or "process.kill");
    }

    [Fact]
    public void IncompleteLockVisibility_IsDifferentFromCompleteEmptyLockQuery()
    {
        var incomplete = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence",
            locks: "visibility restricted; rows may be missing",
            lockState: DiagnosticEvidenceState.Partial));
        var empty = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence",
            locks: "complete=true; rows=[]"));

        Assert.Contains(incomplete.Unknown, item => item.Contains("Lock visibility is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(incomplete.Inferences, item => item.Contains("no relevant lock was observed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(empty.Inferences, item => item.Contains("no relevant lock was observed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(empty.Unknown, item => item.Contains("Lock visibility is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(incomplete.AllReasoning, empty.AllReasoning);
    }

    [Fact]
    public void TargetRace_PreservesInitialExistenceAndDoesNotBecomeToolFailure()
    {
        var raced = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "exists=false; target disappeared after initial stat"));
        var stable = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence",
            locks: "complete=true; rows=[]"));

        Assert.Contains(raced.Observed, item => item.Contains("initial fs.stat observed the target", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(raced.Unknown, item => item.Contains("state changed/raced", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(raced.AllReasoning, item => item.Contains("bOps deleted", StringComparison.OrdinalIgnoreCase) || item.Contains("target never existed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stable.Unknown, item => item.Contains("state changed/raced", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(raced.Requested, item => item == "fs.locks");
    }

    [Fact]
    public void ProcessRace_PreservesEarlierLockObservation()
    {
        var exited = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence",
            locks: "complete=true; holder pid=42 accessKind=file-descriptor",
            inspect: "pid=42 not found; exited before inspection",
            inspectState: DiagnosticEvidenceState.Unavailable));
        var present = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence",
            locks: "complete=true; holder pid=42 accessKind=file-descriptor",
            inspect: "pid=42 exists=true name=editor"));

        Assert.Contains(exited.Inferences, item => item.Contains("lock evidence identifies PID 42", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(exited.Unknown, item => item.Contains("exited before inspection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exited.Inferences, item => item.Contains("never a lock", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(present.Unknown, item => item.Contains("exited before inspection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModuleContext_IsObservedWithoutClaimingItProvesTheLock()
    {
        var mapped = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence",
            locks: "complete=true; holder pid=42 accessKind=file-descriptor",
            inspect: "pid=42 exists=true name=editor",
            modules: "mapped module target.dll"));
        var notMapped = RecordedFileAccessPlanner.Diagnose(Evidence(
            permissions: "current identity has compatible access evidence",
            locks: "complete=true; holder pid=42 accessKind=file-descriptor",
            inspect: "pid=42 exists=true name=editor",
            modules: "no related module observed"));

        Assert.Contains(mapped.Observed, item => item.Contains("module mapping/context", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(notMapped.Observed, item => item.Contains("module mapping/context", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mapped.Inferences, item => item.Contains("lock evidence identifies PID 42", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notMapped.Inferences, item => item.Contains("lock evidence identifies PID 42", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(string.Join(" ", mapped.AllReasoning), "module proves the lock", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitiallyAbsentTarget_IsObservedWithoutInventingAHostCause()
    {
        var decision = RecordedFileAccessPlanner.Diagnose(new DiagnosticScenarioHarness(
            DiagnosticScenarioHarness.Available("fs.stat", "exists=false")));

        Assert.Contains(decision.Observed, item => item.Contains("absent initially", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(decision.Inferences);
        Assert.Equal(["fs.stat"], decision.Requested);
    }

    private static DiagnosticScenarioHarness Evidence(
        string permissions,
        string? locks = null,
        DiagnosticEvidenceState lockState = DiagnosticEvidenceState.Available,
        string? inspect = null,
        DiagnosticEvidenceState inspectState = DiagnosticEvidenceState.Available,
        string? tree = null,
        string? modules = null)
    {
        var evidence = new List<DiagnosticEvidence>
        {
            DiagnosticScenarioHarness.Available("fs.stat", "exists=true; type=file; modifiedUtc=2026-09-24T10:00:00Z"),
            DiagnosticScenarioHarness.Available("fs.permissions", permissions),
        };
        if (locks is not null) evidence.Add(new DiagnosticEvidence("fs.locks", lockState, locks));
        if (inspect is not null) evidence.Add(new DiagnosticEvidence("process.inspect", inspectState, inspect));
        if (tree is not null) evidence.Add(DiagnosticScenarioHarness.Available("process.tree", tree));
        if (modules is not null) evidence.Add(DiagnosticScenarioHarness.Available("process.modules", modules));
        return new DiagnosticScenarioHarness(evidence.ToArray());
    }
}

internal sealed record FileAccessDecision(
    IReadOnlyList<string> Requested,
    IReadOnlyList<string> Observed,
    IReadOnlyList<string> Inferences,
    IReadOnlyList<string> Unknown)
{
    public IReadOnlyList<string> AllReasoning => [.. Observed, .. Inferences, .. Unknown];
}

internal static class RecordedFileAccessPlanner
{
    public static FileAccessDecision Diagnose(DiagnosticScenarioHarness harness)
    {
        var observed = new List<string>();
        var inferences = new List<string>();
        var unknown = new List<string>();
        DiagnosticEvidence Get(string capability)
        {
            var evidence = harness.Request(capability);
            observed.Add($"{capability}: {evidence.Observation} [{evidence.State}]");
            return evidence;
        }

        var stat = Get("fs.stat");
        if (!stat.IsComplete)
        {
            unknown.Add("Target existence could not be established because fs.stat is incomplete.");
            return Decision();
        }
        if (Contains(stat, "exists=false"))
        {
            observed.Add("Target was absent initially; no file-access cause is inferred.");
            return Decision();
        }
        observed.Add("Initial fs.stat observed the target exists.");

        var permissions = Get("fs.permissions");
        if (Contains(permissions, "disappeared") || Contains(permissions, "exists=false"))
        {
            unknown.Add("Observed target state changed/raced after initial stat; reconsider the operation against current state, not stale evidence.");
            return Decision();
        }
        if (Contains(permissions, "denied"))
        {
            inferences.Add("Permission/access evidence explains why the current identity may not replace or delete the target; this is not lock evidence.");
        }
        else if (!permissions.IsComplete)
        {
            unknown.Add("Permission/access visibility is incomplete; platform-specific effective access remains unknown.");
        }

        var locks = Get("fs.locks");
        if (!locks.IsComplete)
        {
            unknown.Add("Lock visibility is incomplete; lock ownership remains unknown and no absence conclusion is valid.");
            return Decision();
        }
        if (!Contains(locks, "holder pid="))
        {
            inferences.Add("No relevant lock was observed by the available complete lock source; replacement/deletion may still fail for other filesystem semantics or a race.");
            return Decision();
        }

        var pid = locks.Observation.Split("holder pid=", StringSplitOptions.None)[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        inferences.Add($"Lock evidence identifies PID {pid} as associated with the target; it does not establish malicious or broken process behavior.");
        if (!harness.HasNext("process.inspect"))
        {
            unknown.Add("Current process identity was not inspected.");
            return Decision();
        }
        var inspect = Get("process.inspect");
        if (!inspect.IsComplete || Contains(inspect, "not found") || Contains(inspect, "exited"))
        {
            unknown.Add($"Lock holder PID {pid} exited before inspection; the earlier lock observation remains valid, but current identity cannot be confirmed.");
            return Decision();
        }
        observed.Add($"process.inspect confirms the current identity for lock-holder PID {pid}.");
        if (harness.HasNext("process.tree")) Get("process.tree");
        if (harness.HasNext("process.modules"))
        {
            var modules = Get("process.modules");
            if (Contains(modules, "mapped module target"))
                observed.Add("Module mapping/context is observed; a mapped module is not equivalent to a proven write/delete lock.");
        }
        return Decision();

        FileAccessDecision Decision() => new(harness.RequestedCapabilities, observed, inferences, unknown);
    }

    private static bool Contains(DiagnosticEvidence evidence, string value) =>
        evidence.Observation.Contains(value, StringComparison.OrdinalIgnoreCase);
}
