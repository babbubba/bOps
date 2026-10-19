// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and process passes to the scenario, which disposes it before returning.
#pragma warning disable CA2000

using bOps.Abstractions;
using bOps.AgentFramework.Tests.PromptAssembly;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;
using bOps.Runtime.Tests;

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>AE-1: a policy that forbids exactly one Read tool and leaves every other Read tool automatic (the runtime's own default for the rest).</summary>
internal sealed class AeDenyToolPolicy(string forbiddenTool) : IPolicyEngine
{
    public PolicyDecision Evaluate(PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Manifest.Name == forbiddenTool
            ? new PolicyDecision(PolicyMode.Forbidden, "AE-1 scripted policy: this tool is forbidden")
            : new DefaultTestPolicyEngine().Evaluate(context);
    }
}

/// <summary>AE-1: the synthetic tools of the scripted scenarios. Names, descriptions and outputs are invented; no machine, user or path is named.</summary>
internal static class AeFixtures
{
    internal const string DiskGoal = "Find which folders of the current user's profile use the most disk space.";
    internal const string SensorGoal = "Report the temperature that the machine's sensors show right now.";

    /// <summary>The refusal of the synthetic size tool: the stand-in for a filesystem package whose path policy permits no path.</summary>
    internal const string PathRefusal = "ERROR: Path not permitted: no path pattern is configured for reading.";

    private static ToolCallResult Complete(string output) =>
        ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Complete };

    private static PaTool Plain(string name, string description, string output) => new(name, description, Complete(output), []);

    internal static PaTool Logs() => Plain("test.logs", "Reads the synthetic log summary. Read-only.", "log summary: 3 warnings, 0 errors in the last hour");

    internal static PaTool Extra() => Plain("test.extra", "Reads the synthetic extra counters. Read-only.", "extra counters: queue 2, retries 0");

    internal static PaTool Whoami() => Plain(
        "test.whoami", "Reads the synthetic identity of the current user. Read-only.", "user: synthetic-user; the profile location is not reported");

    /// <summary>
    /// The stand-in for <c>fs.size</c> (T06): a Read tool whose own path policy permits nothing, so it fails on every call. The refusal
    /// is scripted, not produced by the real filesystem package, and no policy is changed to make it succeed.
    /// </summary>
    internal static PaTool Size() => new(
        "test.size", "Measures the size of a synthetic folder. Read-only; subject to the path policy.",
        ToolCallResult.Failure(PathRefusal),
        [new ToolParameter("path", ToolParameterType.String, "Folder to measure.", Required: false)]);

    internal static PaTool Status() => Plain("test.read", "Reads the synthetic service status. Read-only.", "service alpha: running; restarts 0; uptime 4d");
}

/// <summary>
/// AE-1: the scripted scenarios. Each is a fixed list of model answers driven through the real pipeline of PA-1
/// (<c>PaCompose</c>: AgentRunner, broker, MAF projector, the real OpenAI-compatible adapter, an offline upstream); only the model's
/// answers and the tools' output are scripted. The numbered comments are the provider requests of the run (0 = planning).
/// </summary>
internal static class AeScenarios
{
    internal const string Premature = "The checks so far look fine, so nothing else is needed.";
    internal const string Final = "All planned checks ran: the synthetic service is running and nothing needs attention.";
    internal const string TruncatedPlan = "{\"rationale\":\"the remaining work\",\"steps\":[{\"description\":\"";

    internal const string ProseProposal =
        "I could not read any sensor temperature. To answer I propose one more check: system.sensor with {\"kind\":\"temperature\"}.";

    internal const string AbsentCapability = "system.sensor";

    internal const string DeferredFolderCheck =
        "The disk usage and the user identity are known, but the largest profile folders are not: the identity tool reports no profile location. "
        + "A size check on the profile (test.size) would be the next step.";

    internal const string HonestSizeLimit =
        "The disk usage and the user identity are known. The folder size check was refused by the path policy, so the largest profile folders "
        + "were not measured and remain unknown.";

    internal const string HonestSensorLimit =
        "No tool of this host reports sensor temperatures, so the temperature was not measured and remains unknown.";

    private static AeRun Observe(string id, PaSetup setup, PaRun run) => AeObserver.Observe(id, setup.Script, run);

    private static async Task<AeRun> Run(string id, PaSetup setup) => Observe(id, setup, await PaCompose.RunAsync(setup));

    /// <summary>The synthetic tools of the T02/T10 scenarios: three operational checks and one the replan adds.</summary>
    private static IReadOnlyList<PaTool> OperationalTools() => [PaFixtures.Read(), PaFixtures.Other(), AeFixtures.Logs(), AeFixtures.Extra()];

    // ---- T02 ----

    /// <summary>
    /// AE1-T02: premature finals, a replan that adds a step, a paid replan cut at the output limit, the LIVE-05 continuation, and a third
    /// premature final whose replan is not paid again. Requests: 0 plan [read, other, logs]; 1 step 0; 2 premature #1; 3 early-final
    /// replan (revision 1: other, logs, extra); 4 other; 5 premature #2; 6 truncated replan; 7 logs (continuation, cursor 1);
    /// 8 premature #3; 9 extra (continuation, cursor 2, no replan call); 10 final.
    /// </summary>
    internal static Task<AeRun> T02PrematureFinals() => Run("AE1-T02", new PaSetup(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, the disk and the logs.", "test.read", "test.other", "test.logs"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Text(Premature),
        PaReply.Plan("Add a cross-check of the extra counters after the logs.", "test.other", "test.logs", "test.extra"),
        PaReply.Call("call-2", "test.other"),
        PaReply.Text(Premature),
        PaReply.Truncated(TruncatedPlan),
        PaReply.Call("call-3", "test.logs"),
        PaReply.Text(Premature),
        PaReply.Call("call-4", "test.extra"),
        PaReply.Text(Final),
    ])
    {
        Tools = OperationalTools(),
    });

    /// <summary>
    /// AE1-T02-LOOP: the model answers prematurely every time and the replan returns the same plan, so nothing progresses. Requests: 0 plan
    /// [read, other, logs]; then premature final / identical replan, three times; 7 the fourth premature final ends the task.
    /// </summary>
    internal static Task<AeRun> T02NoProgressLoop() => Run("AE1-T02-LOOP", new PaSetup(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, the disk and the logs.", "test.read", "test.other", "test.logs"),
        PaReply.Text(Premature),
        PaReply.Plan("The same plan again.", "test.read", "test.other", "test.logs"),
        PaReply.Text(Premature),
        PaReply.Plan("The same plan again.", "test.read", "test.other", "test.logs"),
        PaReply.Text(Premature),
        PaReply.Plan("The same plan again.", "test.read", "test.other", "test.logs"),
        PaReply.Text(Premature),
    ])
    {
        Tools = OperationalTools(),
    });

    /// <summary>
    /// AE1-T02-EMPTY: the early-final replan is answered with an empty plan, which the runtime accepts as a formal revision; the two
    /// remaining checks never run. Requests: 0 plan [read, other, logs]; 1 step 0; 2 premature final; 3 empty replan; 4 final.
    /// </summary>
    internal static Task<AeRun> T02EmptyPlanWayOut() => Run("AE1-T02-EMPTY", new PaSetup(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, the disk and the logs.", "test.read", "test.other", "test.logs"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Text(Premature),
        PaReply.Plan("Nothing further is needed."),
        PaReply.Text(Final),
    ])
    {
        Tools = OperationalTools(),
    });

    /// <summary>
    /// AE1-T02-POLICY: the LIVE-05 continuation lands on a tool that policy forbids. Requests: 0 plan [read, logs, other]; 1 step 0;
    /// 2 premature final; 3 truncated replan; 4 logs (continuation, cursor 1: denied by policy); 5 replan after the denial
    /// (other); 6 other; 7 final.
    /// </summary>
    internal static Task<AeRun> T02ContinuationUnderPolicy() => Run("AE1-T02-POLICY", new PaSetup(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, the logs and the disk.", "test.read", "test.logs", "test.other"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Text(Premature),
        PaReply.Truncated(TruncatedPlan),
        PaReply.Call("call-2", "test.logs"),
        PaReply.Plan("The logs are not available; read the disk.", "test.other"),
        PaReply.Call("call-3", "test.other"),
        PaReply.Text(Final),
    ])
    {
        Tools = OperationalTools(),
        Policy = new AeDenyToolPolicy("test.logs"),
    });

    /// <summary>The healthy control of the T02 rubric: the same plan, no premature final.</summary>
    internal static Task<AeRun> T02CleanRun() => Run("AE1-T02-CLEAN", new PaSetup(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, the disk and the logs.", "test.read", "test.other", "test.logs"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Call("call-2", "test.other"),
        PaReply.Call("call-3", "test.logs"),
        PaReply.Text(Final),
    ])
    {
        Tools = OperationalTools(),
    });

    // ---- P1 ----

    /// <summary>
    /// AE1-P1: the model calls the tool of a LATER planned step while the earlier step's tool is the only one offered (the confusion the
    /// audit read in the reasoning of T04 and T05). The misroute is scripted from that observation; AE-1 measures what the runtime does with
    /// it, not whether the prompt causes it. Requests: 0 plan [read, other, logs]; 1 native call to test.other (not offered); 2 test.read
    /// after the in-place correction; 3 other; 4 logs; 5 final.
    /// </summary>
    internal static Task<AeRun> P1CallsALaterStepsTool() => Run("AE1-P1", new PaSetup(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, the disk and the logs.", "test.read", "test.other", "test.logs"),
        PaReply.Call("call-1", "test.other"),
        PaReply.Call("call-2", "test.read"),
        PaReply.Call("call-3", "test.other"),
        PaReply.Call("call-4", "test.logs"),
        PaReply.Text(Final),
    ])
    {
        Tools = OperationalTools(),
    });

    // ---- T05 ----

    /// <summary>
    /// AE1-T05: the model reads persisted evidence at plan end, its next reply is cut at the output limit with no content, the runtime's
    /// recovery turn follows, the model reads the same evidence again and then answers with <c>&gt;</c> (finish_reason stop). Requests:
    /// 0 plan [read]; 1 step 0 (large result); 2 evidence read #1; 3 output-limited reply; 4 evidence read #2 (after the recovery
    /// turn); 5 the <c>&gt;</c> reply. With <paramref name="retries"/> = 0 the run ends at request 3.
    /// </summary>
    internal static Task<AeRun> T05LengthThenGreaterThan(int retries = 1) => Run(
        retries == 1 ? "AE1-T05" : $"AE1-T05-R{retries}",
        new PaSetup(PaFixtures.Goal,
        [
            PaReply.Plan("Read the service.", "test.read"),
            PaReply.Call("call-1", "test.read"),
            PaReply.EvidenceRead("read-1", 0),
            PaReply.OutputLimited(),
            PaReply.EvidenceRead("read-2", 0),
            PaReply.Text(">"),
        ])
        {
            Tools = [PaFixtures.Read(PaFixtures.LongListing())],
            Options = new AgentRunnerOptions { EmptyFinalResponseRetries = retries },
        }
        .TrimScriptAfterFailureWhen(retries == 0, 4));

    /// <summary>The healthy control of the T05 rubric: the same evidence read, then a substantive final.</summary>
    internal static Task<AeRun> T05CleanRun() => Run("AE1-T05-CLEAN", new PaSetup(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service.", "test.read"),
        PaReply.Call("call-1", "test.read"),
        PaReply.EvidenceRead("read-1", 0),
        PaReply.Text(Final),
    ])
    {
        Tools = [PaFixtures.Read(PaFixtures.LongListing())],
    });

    // ---- T06 ----

    private static IReadOnlyList<PaTool> DiskTools() => [PaFixtures.Other(), AeFixtures.Whoami(), AeFixtures.Size()];

    /// <summary>
    /// AE1-T06 variant A: the deferred check IS requested through a channel the runtime recognizes: at plan end the model calls the
    /// tool natively, the runtime replans, the revised plan holds the check, the tool runs and its own path policy refuses. Requests:
    /// 0 plan [other, whoami]; 1 other; 2 whoami; 3 native call to test.size (not offered); 4 replan [size]; 5 size (refused by the
    /// tool); 6 final.
    /// </summary>
    internal static Task<AeRun> T06CheckRequestedNatively() => Run("AE1-T06-A", new PaSetup(AeFixtures.DiskGoal,
    [
        PaReply.Plan("Read the disk usage, then the identity.", "test.other", "test.whoami"),
        PaReply.Call("call-1", "test.other"),
        PaReply.Call("call-2", "test.whoami"),
        PaReply.Call("call-3", "test.size"),
        PaReply.Plan("Measure the profile folders.", "test.size"),
        PaReply.Call("call-4", "test.size"),
        PaReply.Text(AeScenarios.HonestSizeLimit),
    ])
    {
        Tools = DiskTools(),
    });

    /// <summary>
    /// AE1-T06 variant B: the deferred check is only described in the final prose. Requests: 0 plan [other, whoami]; 1 other; 2 whoami;
    /// 3 final that names test.size as a next step and calls nothing.
    /// </summary>
    internal static Task<AeRun> T06CheckOnlyDescribed() => Run("AE1-T06-B", new PaSetup(AeFixtures.DiskGoal,
    [
        PaReply.Plan("Read the disk usage, then the identity.", "test.other", "test.whoami"),
        PaReply.Call("call-1", "test.other"),
        PaReply.Call("call-2", "test.whoami"),
        PaReply.Text(DeferredFolderCheck),
    ])
    {
        Tools = DiskTools(),
    });

    // ---- T08 ----

    /// <summary>
    /// AE1-T08 variant A: at plan end the model proposes the absent tool <c>system.sensor</c> in its final prose and makes no native
    /// call. Requests: 0 plan [read]; 1 step 0; 2 final prose that proposes system.sensor.
    /// </summary>
    internal static Task<AeRun> T08ProposedInProse() => Run("AE1-T08-A", new PaSetup(AeFixtures.SensorGoal,
    [
        PaReply.Plan("Read the service status.", "test.read"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Text(ProseProposal),
    ])
    {
        Tools = [AeFixtures.Status()],
    });

    /// <summary>
    /// AE1-T08 variant B: the same plan end, but the model calls the absent tool natively. Requests: 0 plan [read]; 1 step 0; 2 native
    /// call to system.sensor; 3 replan (empty); 4 final.
    /// </summary>
    internal static Task<AeRun> T08CalledNatively() => Run("AE1-T08-B", new PaSetup(AeFixtures.SensorGoal,
    [
        PaReply.Plan("Read the service status.", "test.read"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Call("call-2", AbsentCapability),
        PaReply.Plan("No tool reports sensors."),
        PaReply.Text(HonestSensorLimit),
    ])
    {
        Tools = [AeFixtures.Status()],
    });

    // ---- T10 ----

    /// <summary>
    /// AE1-T10: an authorized, pinned synthetic knowledge pack is read before acting, and the model then answers prematurely three times
    /// while operational steps remain. Requests: 0 plan [read, other, logs]; 1 step 0 with the index (draws the knowledge read);
    /// 2 continuation after the read (premature #1, cursor 0); 3 truncated replan; 4 read (continuation, cursor 0); 5 premature #2 (cursor
    /// 1, replan not paid again); 6 other (continuation); 7 premature #3 (cursor 2); 8 logs (continuation); 9 final.
    /// </summary>
    internal static async Task<AeRun> T10KnowledgeThenPrematureFinals()
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack { Kind = PackKind.Knowledge };
        var raw = pack.Build();
        world.Source.Add(KnowledgeRunnerWorld.TenantA, pack.PackId, pack.Version, () => raw);
        var reference = new PackReference(pack.PackId, pack.Version);
        using var process = world.Boot(
            [new PackGrant(KnowledgeRunnerWorld.TenantA, reference, pack.Kind)],
            bindings: new Dictionary<TenantId, PackReference> { [KnowledgeRunnerWorld.TenantA] = reference });
        var setup = new PaSetup("diagnose nginx",
        [
            PaReply.Plan("Read the service, the disk and the logs.", "test.read", "test.other", "test.logs"),
            PaReply.Knowledge("k1", "read_resource", "nginx-triage", "references/checklist.md"),
            PaReply.Text(Premature),
            PaReply.Truncated(TruncatedPlan),
            PaReply.Call("call-1", "test.read"),
            PaReply.Text(Premature),
            PaReply.Call("call-2", "test.other"),
            PaReply.Text(Premature),
            PaReply.Call("call-3", "test.logs"),
            PaReply.Text(Final),
        ])
        {
            Tools = OperationalTools(),
            KnowledgeHost = process.Host,
            Store = process.Store,
            Actor = KnowledgeRunnerWorld.ActorA,
        };
        var run = await PaCompose.RunAsync(setup);
        return AeObserver.Observe("AE1-T10", setup.Script, run, [.. world.Events]);
    }
}

/// <summary>AE-1: small script transformations used by the scenarios above.</summary>
internal static class AeSetupExtensions
{
    /// <summary>Keeps only the first <paramref name="keep"/> scripted answers when <paramref name="condition"/> holds (a run that ends early).</summary>
    internal static PaSetup TrimScriptAfterFailureWhen(this PaSetup setup, bool condition, int keep) =>
        condition ? setup with { Script = [.. setup.Script.Take(keep)] } : setup;
}
