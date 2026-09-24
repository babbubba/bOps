// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>Recorded, evidence-dependent L8 reasoning; this scenario only diagnoses and never mutates Docker or the host.</summary>
public sealed class DockerHostedServiceFailureScenarioTests
{
    private static readonly string[] Sequence = ["docker.containers", "docker.inspect", "docker.logs", "docker.images", "network.sockets", "storage.io", "system.events"];

    [Fact]
    public void StoppedContainer_IsObservedWithoutCallingItACrash_AndRunningRemovesTheConclusion()
    {
        var stopped = RecordedDockerFailurePlanner.Diagnose(Evidence(containers: "target=present; state=exited; running=false", inspect: "image=acme/api:1; state=exited; exitCode=1", logs: "timestamp=2026-09-24T10:00:00Z; source=container; error=startup failed", images: "tags=acme/api:1", sockets: "listener=absent"));
        var running = RecordedDockerFailurePlanner.Diagnose(Evidence(containers: "target=present; state=running; running=true", inspect: "image=acme/api:1; state=running", logs: "no matching error", images: "tags=acme/api:1", sockets: "listener=present"));

        Assert.Contains(stopped.Observed, item => item.Contains("container state is exited", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(stopped.Observed, item => item.Contains("exit evidence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stopped.AllReasoning, item => item.Contains("crashed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(running.AllReasoning, item => item.Contains("container state is exited", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(running.Observed, item => item.Contains("container is running", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InspectAndImageEvidence_CorrelateOnlyWhenTheirReferencesMatch()
    {
        var matching = RecordedDockerFailurePlanner.Diagnose(Evidence(inspect: "image=acme/api:1; state=running", images: "tags=acme/api:1", sockets: "listener=present", logs: "no matching error"));
        var changed = RecordedDockerFailurePlanner.Diagnose(Evidence(inspect: "image=acme/api:2; state=running", images: "tags=acme/api:1", sockets: "listener=present", logs: "no matching error"));

        Assert.Contains(matching.Observed, item => item.Contains("configured image acme/api:1 is present", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(changed.Unknown, item => item.Contains("configured image acme/api:2 was not observed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(matching.AllReasoning, item => item.Contains("correct image", StringComparison.OrdinalIgnoreCase) || item.Contains("corrupt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(changed.AllReasoning, item => item.StartsWith("Wrong image caused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunningContainerWithoutListener_IsNotStoppedOrApplicationCrash_AndListenerRemovesTheConclusion()
    {
        var absent = RecordedDockerFailurePlanner.Diagnose(Evidence(sockets: "listener=absent", logs: "no matching error"));
        var present = RecordedDockerFailurePlanner.Diagnose(Evidence(sockets: "listener=present", logs: "no matching error"));

        Assert.Contains(absent.Observed, item => item.Contains("expected host listener was not observed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(absent.AllReasoning, item => item.Contains("container state is exited", StringComparison.OrdinalIgnoreCase) || item.Contains("application crashed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(present.AllReasoning, item => item.Contains("expected host listener was not observed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(present.Unknown, item => item.Contains("listener does not establish application health", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StoragePressure_IsCorrelatedOnly_AndRemovingItRemovesTheCorrelation()
    {
        var pressure = RecordedDockerFailurePlanner.Diagnose(Evidence(storage: "pressure=true; latencyMs=200; queueDepth=32", logs: "no matching error"));
        var normal = RecordedDockerFailurePlanner.Diagnose(Evidence(storage: "pressure=false; latencyMs=2; queueDepth=0", logs: "no matching error"));

        Assert.Contains(pressure.Inferences, item => item.Contains("CORRELATED / possible contributing condition", StringComparison.Ordinal));
        Assert.DoesNotContain(pressure.AllReasoning, item => item.StartsWith("Storage caused", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(normal.AllReasoning, item => item.Contains("possible contributing condition", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DockerDaemonUnavailable_IsNotAnEmptyContainerResult()
    {
        var unavailable = RecordedDockerFailurePlanner.Diagnose(Evidence(containers: "Docker backend unavailable", containersState: DiagnosticEvidenceState.Unavailable, inspectState: DiagnosticEvidenceState.Unavailable, logsState: DiagnosticEvidenceState.Unavailable, imagesState: DiagnosticEvidenceState.Unavailable));
        var empty = RecordedDockerFailurePlanner.Diagnose(Evidence(containers: "target=absent; items=[]", logs: "no matching error"));

        Assert.Contains(unavailable.Unknown, item => item.Contains("Docker runtime state cannot be established", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(unavailable.AllReasoning, item => item.Contains("target container was not observed", StringComparison.OrdinalIgnoreCase) || item.Contains("container state is", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(empty.Observed, item => item.Contains("target container was not observed in the complete query", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TargetAbsent_IsDistinctFromDaemonUnavailable_AndPresentRemovesTheConclusion()
    {
        var absent = RecordedDockerFailurePlanner.Diagnose(Evidence(containers: "target=absent; items=[]", logs: "no matching error"));
        var present = RecordedDockerFailurePlanner.Diagnose(Evidence(containers: "target=present; state=running; running=true", logs: "no matching error"));

        Assert.Contains(absent.Observed, item => item.Contains("target container was not observed in the complete query", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(present.AllReasoning, item => item.Contains("target container was not observed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(absent.AllReasoning, item => item.Contains("deleted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompleteLogsWithoutError_AreDistinctFromUnavailableLogs_AndErrorEvidenceChangesWithFixture()
    {
        var error = RecordedDockerFailurePlanner.Diagnose(Evidence(logs: "timestamp=2026-09-24T10:00:00Z; source=stderr; error=bind failed"));
        var empty = RecordedDockerFailurePlanner.Diagnose(Evidence(logs: "no matching error"));
        var unavailable = RecordedDockerFailurePlanner.Diagnose(Evidence(logs: "log source unavailable", logsState: DiagnosticEvidenceState.Unavailable));

        Assert.Contains(error.Observed, item => item.Contains("log failure evidence", StringComparison.OrdinalIgnoreCase) && item.Contains("timestamp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(empty.AllReasoning, item => item.Contains("log failure evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(empty.Observed, item => item.Contains("no matching error was observed in complete logs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(unavailable.Unknown, item => item.Contains("log evidence is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(unavailable.AllReasoning, item => item.Contains("no matching error was observed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MatchingSystemEvent_IsCorrelationOnly_AndRemovingItRemovesTheConclusion()
    {
        var matching = RecordedDockerFailurePlanner.Diagnose(Evidence(events: "timestamp=2026-09-24T10:00:05Z; source=docker; container=api; message=daemon warning", logs: "no matching error"));
        var unrelated = RecordedDockerFailurePlanner.Diagnose(Evidence(events: "timestamp=2026-09-24T10:00:05Z; source=kernel; message=unrelated telemetry", logs: "no matching error"));

        Assert.Contains(matching.Inferences, item => item.Contains("matching system event", StringComparison.OrdinalIgnoreCase) && item.Contains("CORRELATED", StringComparison.Ordinal));
        Assert.DoesNotContain(matching.AllReasoning, item => item.StartsWith("ROOT CAUSE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(unrelated.AllReasoning, item => item.Contains("matching system event", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunningContainerAndListenerPresent_LeavesApplicationHealthUnknown()
    {
        var decision = RecordedDockerFailurePlanner.Diagnose(Evidence(logs: "no matching error", storage: "pressure=false", events: "no matching event"));

        Assert.Contains(decision.Observed, item => item.Contains("container is running", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Observed, item => item.Contains("expected host listener is present", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Unknown, item => item.Contains("application health", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(decision.AllReasoning, item => item.Contains("application is healthy", StringComparison.OrdinalIgnoreCase) || item.Contains("API request succeeds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompleteCompatibleHostEvidence_LeavesNoRootCauseConclusionWithoutOverclaimOrDockerMutation()
    {
        var decision = RecordedDockerFailurePlanner.Diagnose(Evidence(logs: "no matching error", storage: "pressure=false", events: "no matching event"));

        Assert.Equal(Sequence, decision.Requested);
        Assert.Contains(decision.Inferences, item => item.Contains("No Docker/host-side root cause was established by the available evidence.", StringComparison.Ordinal));
        Assert.DoesNotContain(string.Join(' ', decision.AllReasoning), "application is healthy", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(decision.Requested, item => item is "docker.exec" or "docker.create" or "docker.compose" or "docker.prune" or "docker.restart" or "docker.start" or "docker.stop" or "docker.pull" or "docker.build" or "docker.image.remove");
    }

    private static DiagnosticScenarioHarness Evidence(
        string containers = "target=present; state=running; running=true", DiagnosticEvidenceState containersState = DiagnosticEvidenceState.Available,
        string inspect = "image=acme/api:1; state=running; ports=8080", DiagnosticEvidenceState inspectState = DiagnosticEvidenceState.Available,
        string logs = "no matching error", DiagnosticEvidenceState logsState = DiagnosticEvidenceState.Available,
        string images = "tags=acme/api:1", DiagnosticEvidenceState imagesState = DiagnosticEvidenceState.Available,
        string sockets = "listener=present; endpoint=0.0.0.0:8080", DiagnosticEvidenceState socketsState = DiagnosticEvidenceState.Available,
        string storage = "pressure=false", DiagnosticEvidenceState storageState = DiagnosticEvidenceState.Available,
        string events = "no matching event", DiagnosticEvidenceState eventsState = DiagnosticEvidenceState.Available) => new(
            new DiagnosticEvidence("docker.containers", containersState, containers),
            new DiagnosticEvidence("docker.inspect", inspectState, inspect),
            new DiagnosticEvidence("docker.logs", logsState, logs),
            new DiagnosticEvidence("docker.images", imagesState, images),
            new DiagnosticEvidence("network.sockets", socketsState, sockets),
            new DiagnosticEvidence("storage.io", storageState, storage),
            new DiagnosticEvidence("system.events", eventsState, events));
}

internal sealed record DockerFailureDecision(IReadOnlyList<string> Requested, IReadOnlyList<string> Observed, IReadOnlyList<string> Inferences, IReadOnlyList<string> Unknown)
{
    public IReadOnlyList<string> AllReasoning => [.. Observed, .. Inferences, .. Unknown];
}

internal static class RecordedDockerFailurePlanner
{
    public static DockerFailureDecision Diagnose(DiagnosticScenarioHarness harness)
    {
        var observed = new List<string>();
        var inferences = new List<string>();
        var unknown = new List<string>();
        DiagnosticEvidence Get(string capability) { var evidence = harness.Request(capability); observed.Add($"{capability}: {evidence.Observation} [{evidence.State}]"); return evidence; }

        var containers = Get("docker.containers");
        var inspect = Get("docker.inspect");
        var logs = Get("docker.logs");
        var images = Get("docker.images");
        var sockets = Get("network.sockets");
        var storage = Get("storage.io");
        var events = Get("system.events");

        var targetPresent = containers.IsComplete && Has(containers, "target=present");
        var running = targetPresent && (Has(containers, "state=running") || Has(containers, "running=true"));
        if (!containers.IsComplete)
            unknown.Add("Docker runtime state cannot be established because docker.containers is unavailable or incomplete; container and image state remain UNKNOWN, not empty.");
        else if (!targetPresent)
            observed.Add("OBSERVED: target container was not observed in the complete query; this does not establish deletion or Docker daemon unavailability.");
        else if (running)
            observed.Add("OBSERVED: target container is running; running is container state, not application health or listener evidence.");
        else
            observed.Add("OBSERVED: target container state is exited/stopped/not running; this is not, by itself, a proven application crash.");

        if (!inspect.IsComplete)
            unknown.Add("Inspect configuration/state evidence is incomplete; no configured-image or port conclusion is valid.");
        else if (targetPresent && Value(inspect, "image") is { } configuredImage)
        {
            observed.Add($"OBSERVED: inspect reports configured image {configuredImage}; configuration is distinct from current behavior.");
            if (Value(inspect, "exitCode") is { } exitCode && exitCode != "0")
                observed.Add($"OBSERVED: inspect exit evidence reports exitCode={exitCode}; it adds termination context without naming an internal cause.");
        }

        if (!logs.IsComplete)
            unknown.Add("Container log evidence is incomplete or unavailable; it cannot establish that there were no application errors.");
        else if (Has(logs, "error="))
            observed.Add("OBSERVED: log failure evidence is present with the reported timestamp/source/message; generic error text is not a root-cause conclusion.");
        else if (Has(logs, "no matching error"))
            observed.Add("OBSERVED: no matching error was observed in complete logs for this bounded query.");

        if (!images.IsComplete)
            unknown.Add("Image inventory evidence is incomplete; image presence cannot be established.");
        else if (targetPresent && Value(inspect, "image") is { } image)
        {
            if (Has(images, $"tags={image}"))
                observed.Add($"OBSERVED: configured image {image} is present in the reported inventory; presence does not prove correctness, security, or application behavior.");
            else
                unknown.Add($"Configured image {image} was not observed in the complete inventory; this is not proof of corruption or that a wrong image caused the symptom.");
        }

        if (!sockets.IsComplete)
            unknown.Add("Host listener evidence is incomplete; listener state cannot be established.");
        else if (running && Has(sockets, "listener=absent"))
            observed.Add("OBSERVED: expected host listener was not observed while the container is running; local socket evidence does not establish remote reachability or an application crash.");
        else if (running && Has(sockets, "listener=present"))
            observed.Add("OBSERVED: expected host listener is present while the container is running; listener presence is not application health.");

        if (!storage.IsComplete)
            unknown.Add("Host storage I/O evidence is incomplete; storage pressure cannot be ruled out.");
        else if (Has(storage, "pressure=true"))
            inferences.Add("Host storage pressure is CORRELATED / possible contributing condition only; it does not establish that storage caused the Docker or application symptom.");

        var matchingEvent = events.IsComplete && Has(events, "container=");
        if (!events.IsComplete)
            unknown.Add("System-event evidence is incomplete; matching host or Docker events cannot be ruled out.");
        else if (matchingEvent)
            inferences.Add("A matching system event is CORRELATED supporting evidence; temporal coincidence does not establish Docker or application root cause.");

        if (running && sockets.IsComplete && Has(sockets, "listener=present"))
            unknown.Add("Container and host listener evidence are present, but listener does not establish application health, API success, database health, authentication, or protocol-level health.");

        if (targetPresent && running && logs.IsComplete && Has(logs, "no matching error") && images.IsComplete && Value(inspect, "image") is { } expected && Has(images, $"tags={expected}") && sockets.IsComplete && Has(sockets, "listener=present") && storage.IsComplete && !Has(storage, "pressure=true") && events.IsComplete && !matchingEvent)
            inferences.Add("No Docker/host-side root cause was established by the available evidence.");

        return new DockerFailureDecision(harness.RequestedCapabilities, observed, inferences, unknown);
    }

    private static bool Has(DiagnosticEvidence evidence, string text) => evidence.Observation.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static string? Value(DiagnosticEvidence evidence, string key)
    {
        foreach (var segment in evidence.Observation.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase)) return pair[1];
        }

        return null;
    }
}
