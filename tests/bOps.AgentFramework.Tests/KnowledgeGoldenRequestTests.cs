// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and process passes to the test, which disposes the world.
#pragma warning disable CA2000

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// KNOW-DISC-01 T8-FIX01 (ADR-0057 H.1 row 9): the requests the runner builds for planning, for a replan and, without a knowledge
/// binding, for every step are byte-identical to the ones the baseline <c>d3da032</c> (the commit before the index) builds. The
/// expected bytes are checked in under <c>Golden/d3da032/</c>; they were produced by running THIS file, unchanged, against a
/// <c>git archive d3da032</c> copy of the repository with <c>BOPS_GOLDEN_CAPTURE_DIR</c> set, never by running it on the current code
/// (see <c>Golden/d3da032/README.md</c>). The scenario is scripted and offline: a fixed task id, a fixed goal, a scripted model and
/// two read-only test tools. Nothing here depends on time, a credential, a network, the operating system, or a random value.
/// The only fields compared are the ones that exist in both trees: system prompt, history (role, text, tool calls, provenance) and the
/// offered tool manifests, with the platform list masked (it names the operating system the test runs on).
/// </summary>
public sealed class KnowledgeGoldenRequestTests
{
    private const string Goal = "diagnose nginx";
    private const string CaptureVariable = "BOPS_GOLDEN_CAPTURE_DIR";
    private static readonly Guid TaskId = new("5b0f1d2a-3c4e-4f60-8a71-92b3c4d5e6f7");

    private static readonly JsonSerializerOptions Canonical = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static TheoryData<string> Scenarios => ["legacy-nohost", "legacy-notbound", "planning-replan-bound"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task TheRequestsAreByteIdenticalToTheBaseline(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var actual = await RunScenarioAsync(scenario);

        if (Environment.GetEnvironmentVariable(CaptureVariable) is { Length: > 0 } capture)
        {
            // Capture mode: only ever used on the baseline copy. It writes the bytes and compares nothing.
            Directory.CreateDirectory(capture);
            await File.WriteAllTextAsync(Path.Combine(capture, scenario + ".json"), actual, new UTF8Encoding(false));
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "d3da032", scenario + ".json");
        Assert.True(File.Exists(path), $"The golden file {path} is missing; it is produced from a d3da032 archive (see Golden/d3da032/README.md).");
        var expected = (await File.ReadAllTextAsync(path, new UTF8Encoding(false))).ReplaceLineEndings("\n"); // a checkout may have changed the separators between the lines

        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            $"{scenario}: the requests differ from the d3da032 baseline at character {FirstDifference(expected, actual)} "
            + $"(expected sha256 {Sha256(expected)[..12]}, actual {Sha256(actual)[..12]}).");
        Assert.DoesNotContain("[knowledge-index", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runtime.knowledge_read", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScenarioIsReproducible_TwoRunsInTheSameProcessGiveTheSameBytes()
    {
        foreach (var scenario in new[] { "legacy-nohost", "planning-replan-bound" })
        {
            Assert.Equal(await RunScenarioAsync(scenario), await RunScenarioAsync(scenario));
        }
    }

    private static async Task<string> RunScenarioAsync(string scenario)
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack { Kind = PackKind.Knowledge };
        var raw = pack.Build();
        world.Source.Add(KnowledgeRunnerWorld.TenantA, pack.PackId, pack.Version, () => raw);
        var reference = new PackReference(pack.PackId, pack.Version);
        var bindings = scenario == "legacy-notbound"
            ? new Dictionary<TenantId, PackReference>()
            : new Dictionary<TenantId, PackReference> { [KnowledgeRunnerWorld.TenantA] = reference };
        using var process = world.Boot([new PackGrant(KnowledgeRunnerWorld.TenantA, reference, pack.Kind)], bindings: bindings);

        // An early final with a step left is re-evaluated by a governed replan, which is empty: planning, replan and three step requests.
        var rig = process.Runner(
            [Script.Plan("test.read", "test.other"), Script.Tool("c1", "test.read"), Script.Final("too early"), Script.Plan(), Script.Final("done")],
            withHost: scenario != "legacy-nohost");
        var state = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA, TaskId);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(5, rig.Model.Requests.Count);
        var requests = scenario == "planning-replan-bound" ? new[] { rig.Model.Requests[0], rig.Model.Requests[3] } : [.. rig.Model.Requests];
        return string.Join('\n', requests.Select(request => JsonSerializer.Serialize(Project(request), Canonical)));
    }

    /// <summary>The fields that exist in the baseline and in this tree; the platform list names the machine, so it is masked.</summary>
    private static object Project(ModelRequest request) => new
    {
        request.SystemPrompt,
        request.History,
        AvailableTools = request.AvailableTools.Select(tool => tool with { Platforms = ["*"] }).ToArray(),
    };

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static int FirstDifference(string expected, string actual)
    {
        var length = Math.Min(expected.Length, actual.Length);
        for (var index = 0; index < length; index++)
        {
            if (expected[index] != actual[index])
            {
                return index;
            }
        }

        return length;
    }
}
