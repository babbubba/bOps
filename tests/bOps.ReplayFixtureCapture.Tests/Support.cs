// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Policy;

namespace bOps.ReplayFixtureCapture.Tests;

internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;

    public void Set(DateTimeOffset value) => now = value;
}

internal sealed class FakeEnvironment(
    bool elevated = false,
    string user = @"DOMAIN\bob",
    string machine = "TESTBOX-01",
    int? integrityRid = IntegrityLevels.Medium,
    string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
    bool administratorRole = false,
    string elevationType = "Limited") : IEnvironmentProbe
{
    public int Probes { get; private set; }

    public EnvironmentInfo Probe()
    {
        Probes++;
        return new EnvironmentInfo(user, machine, elevated, administratorRole || elevated, "Windows (fake)", 1234, ".NET (fake)",
            new Dictionary<string, BinaryInfo> { ["fake.dll"] = new("fake.dll", new string('a', 64), "1.0") }, sid, integrityRid, elevationType);
    }
}

/// <summary>A collector that never touches the machine: it answers with a synthetic result shaped like the real one and counts every call.</summary>
internal sealed class FakeCollector(ToolManifest manifest, FakeCollectors owner) : ITool
{
    public ToolManifest Manifest => owner.ManifestOverride?.Invoke(manifest) ?? manifest;

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        owner.Calls.Add((Manifest.Name, arguments.ToJson()));
        if (owner.Hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        if (owner.Throw is { } ex)
        {
            throw ex;
        }

        var spec = CapturePlan.All.Concat(CapturePlan.T02C.Skip(10)).Single(q => q.Tool == Manifest.Name
            && ReplayArgumentsLiteral(q.Arguments) == ReplayArgumentsLiteral(arguments.ToJson()));
        owner.Clock.Advance(owner.AdvancePerCall);
        var result = owner.Respond(spec, owner.Clock.GetUtcNow());
        return result;
    }

    private static string ReplayArgumentsLiteral(JsonObject o) => bOps.QwenLiveBaseline.ReplayArguments.Literal(o);
}

internal sealed class FakeCollectors(FakeClock clock) : ICollectorSource
{
    public FakeClock Clock { get; } = clock;

    public List<(string Tool, JsonObject Arguments)> Calls { get; } = [];

    public TimeSpan AdvancePerCall { get; set; } = TimeSpan.FromSeconds(2);

    public bool Hang { get; set; }

    public Exception? Throw { get; set; }

    public Func<ToolManifest, ToolManifest>? ManifestOverride { get; set; }

    public Func<QuerySpec, DateTimeOffset, ToolCallResult> Respond { get; set; } = FakeData.Result;

    public int Created { get; private set; }

    public ITool Create(string tool)
    {
        Created++;
        return new FakeCollector(new RealManifestSource().ManifestOf(tool), this);
    }
}

/// <summary>Synthetic results with the shape of the real tools' JSON (schema versions, window, counts, sources), with a message that carries a host, an account and a profile path.</summary>
internal static class FakeData
{
    public const string Message = @"Logon by TESTBOX-01\bob from C:\Users\bob\AppData\x on TESTBOX-01";

    public static ToolCallResult Result(QuerySpec spec, DateTimeOffset now) => Make(spec, now, null);

    public static ToolCallResult Make(QuerySpec spec, DateTimeOffset now, Action<JsonObject>? edit, ToolResultCompleteness? completeness = null)
    {
        var to = now.UtcDateTime;
        var from = to - spec.RequestedWindow;
        var mode = spec.EffectiveMode;
        var sources = new JsonArray(
            new JsonObject { ["name"] = "windows.channel.Application", ["status"] = "available" },
            new JsonObject { ["name"] = "windows.channel.System", ["status"] = "available" });
        var root = new JsonObject
        {
            ["schemaVersion"] = spec.Expect.SchemaVersion,
            ["status"] = spec.Tool == "system.events" ? "complete" : "partial",
            ["complete"] = spec.Tool == "system.events",
            ["truncated"] = spec.Tool == "system.crashes",
            ["window"] = new JsonObject { ["fromUtc"] = from.ToString("O", CultureInfo.InvariantCulture), ["toUtc"] = to.ToString("O", CultureInfo.InvariantCulture) },
            ["sources"] = sources,
        };
        if (mode is not null)
        {
            root["mode"] = mode;
        }

        switch (spec.Tool, mode)
        {
            case ("system.events", "raw"):
                root["observedEvents"] = 5;
                root["returnedEvents"] = 5;
                root["events"] = new JsonArray(new JsonObject { ["message"] = Message });
                break;
            case ("system.events", _):
                root["observedEvents"] = 40;
                root["observedGroups"] = 12;
                root["returnedGroups"] = 12;
                root["messageDistribution"] = new JsonObject { ["availability"] = "partial", ["describedGroups"] = 5 };
                root["groups"] = new JsonArray(new JsonObject { ["sampleMessage"] = Message });
                break;
            case ("system.crashes", _):
                root["observedItems"] = 54;
                root["observedGroups"] = 16;
                root["returnedGroups"] = 16;
                root["groups"] = new JsonArray();
                break;
            default:
                root["observedGroups"] = 7;
                root["returnedGroups"] = 7;
                root["groups"] = new JsonArray();
                break;
        }

        edit?.Invoke(root);
        var completenessValue = completeness ?? (root["complete"]?.GetValue<bool>() == true ? ToolResultCompleteness.Complete : ToolResultCompleteness.Partial);
        return new ToolCallResult(ToolOutcome.Success, root.ToJsonString(), null)
        {
            Completeness = completenessValue,
            Facts = [new EvidenceFact("fake.fact", "k", ToolParameterType.String, JsonValue.Create(@"C:\Users\bob\x")!)],
        };
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bops-capture-tests-" + Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Path, recursive: true);
    }
}

internal sealed class Rig : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    public Rig(bool elevated = false, IPolicyEngine? policy = null, FakeEnvironment? environment = null)
    {
        Clock = new FakeClock(Start);
        Collectors = new FakeCollectors(Clock);
        Environment = environment ?? new FakeEnvironment(elevated);
        Policy = policy ?? ReadOnlyPolicy.Create();
        Root = new TempDir();
    }

    public FakeClock Clock { get; }

    public FakeCollectors Collectors { get; }

    public FakeEnvironment Environment { get; }

    public IPolicyEngine Policy { get; }

    public TempDir Root { get; }

    public StringWriter Out { get; } = new();

    public StringWriter Err { get; } = new();

    public int CollectorFactoryCalls { get; private set; }

    public bool CollectorFactoryMustNotBeCalled { get; set; }

    public CliApp Cli() => new(
        Out, Err, Environment, new RealManifestSource(),
        () =>
        {
            CollectorFactoryCalls++;
            return CollectorFactoryMustNotBeCalled ? throw new InvalidOperationException("a collector source was requested") : Collectors;
        },
        Policy, Clock, () => "ab12", @"C:\fake\bOps.ReplayFixtureCapture.dll");

    public Task<int> Run(params string[] args) => Cli().RunAsync(args, CancellationToken.None);

    public string OutRoot => System.IO.Path.Combine(Root.Path, "out");

    public static string DefaultPlanHash => CapturePlan.Hash(CapturePlan.Select([]));

    public string SnapshotDir => System.IO.Path.Combine(OutRoot, "T02-B-20261019T090000Z-ab12");

    public Task<int> Capture(params string[] extra) =>
        Run([.. new[] { "capture", "--enable-capture", "--plan-sha256", DefaultPlanHash, "--out-root", OutRoot }, .. extra]);

    public void Dispose() => Root.Dispose();
}
