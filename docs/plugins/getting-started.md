# Writing your first bOps plugin

This walks through what [`samples/bops-sample-plugin/`](../../samples/bops-sample-plugin/)
already is: a real, buildable, purely-demonstrative Skill/Tool plugin. Copy it as a starting point
rather than writing a manifest from scratch. ADR-0020 defines loading and trust; ADR-0025 defines
Skill activation and restricted evidence invocation.

## What a plugin is

One or more .NET assemblies that reference only the published `bOps.Abstractions` NuGet package
— never `bOps.Runtime`, `bOps.Policy`, or any other core project (rule A7's spirit extends to
plugins, even though they are not shipped from this repository). An entry type implements
exactly one of:

- `IToolProvider` — contributes one or more `ITool`s, each with its own `ToolManifest`.
- `ISkillProvider` — contributes deterministic `ICapability` implementations and their Tools;
  because it extends `IToolProvider`, this is one combined provider kind.
- `IModelProviderPackage` — contributes an `IChatModel` factory for one or more provider ids.

Alongside the built assemblies, a `bops-plugin.json` manifest:

```json
{
  "SchemaVersion": 1,
  "Id": "acme.sample-plugin",
  "Publisher": "Acme",
  "Version": "1.0.0",
  "MinHostAbstractionsVersion": "1.1.0",
  "EntryAssembly": "Acme.SamplePlugin.dll",
  "EntryType": "Acme.SamplePlugin.SampleToolProvider",
  "DeclaredCapabilities": ["sample.echo-marker"],
  "Dependencies": [],
  "MaxDeclaredRisk": "Low"
}
```

| Field | Meaning |
|---|---|
| `SchemaVersion` | The manifest format version. Currently must be `1`. |
| `Id` | Your plugin's identifier: lowercase, dot- or hyphen-separated (`acme.sample-plugin`). The `bops.` prefix is reserved for first-party packages — a manifest claiming it is rejected. |
| `Publisher` | Who publishes this. Informational. |
| `Version` | Your plugin's own version. |
| `MinHostAbstractionsVersion` | The lowest `bOps.Abstractions` version you built against. Installation is rejected if the running host is older. |
| `EntryAssembly` | Your main assembly's file name, relative to the plugin's own folder. |
| `EntryType` | The fully qualified type name the loader activates. |
| `DeclaredCapabilities` | What you claim the plugin may register. For a Skill provider this must exactly equal its activated Capability names; any mismatch fails activation. It remains informational for Tool-only and model providers. |
| `Dependencies` | Your own third-party NuGet dependencies, for license inventory. Informational — the loader resolves real dependencies from your plugin's own folder regardless of what you list here. |
| `MaxDeclaredRisk` | The highest `RiskLevel` any of your tools may declare. Informational — the operator's own `policy.yaml` package ceiling is what is actually enforced (rule S3). |

## Writing the tool

```csharp
using bOps.Abstractions;

namespace Acme.SamplePlugin;

public sealed class SampleEchoTool : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "sample.echo",
        Description = "Echoes the given message back, uppercased.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("message", ToolParameterType.String, "The text to echo back.")],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        var message = arguments.GetRequired<string>("message");
        return Task.FromResult(ToolCallResult.Success(message.ToUpperInvariant()));
    }
}
```

A tool that throws is a bug in the tool (agentic/02-coding-standards.md's error model) — return
`ToolCallResult.Failure(...)` for anything an operator could reasonably expect to go wrong.

If your tool's risk is anything other than `Read`, it **must** also declare a
`VerificationSpec` and implement `IVerifiableTool`, or the registry rejects it at enable time —
this is enforced structurally, not by convention (rule B3).

### Declaring argument bounds, failure kinds and completeness

A bound the model must respect belongs in the manifest, not only in the description. `ToolParameter`
has init-only constraints (additive; the positional constructor is unchanged):

| Parameter type | Constraints |
|---|---|
| `Integer`, `Number` | `Minimum`, `Maximum` (inclusive) |
| `String`, `Path` | `MinLength`, `MaxLength` |
| `PathList` | `MinItems`, `MaxItems` |

```csharp
new ToolParameter("limit", ToolParameterType.Integer, "Maximum rows (1-500, default 50).", Required: false)
{
    Minimum = 1,
    Maximum = 500,
}
```

Registration fails if a constraint does not fit the parameter type, is negative where a count is
meant, or has a minimum above its maximum. The runtime then rejects an out-of-range argument
**before policy, approval and execution** with a message the model can act on
(`Argument 'limit' = 900 exceeds maximum 500.`); it never clamps. Both provider adapters project
the same bounds, the enum values and `additionalProperties: false` into the tool's JSON Schema.
Keep your own validation as defence in depth, and keep the description human-readable: an
architecture test fails when a description states a numeric range (`1-500`, `1..500`, `1 to 500`,
`1 through 500`) that the parameter does not declare.

A bound that depends on host configuration (a ceiling an operator can change) cannot be a static
constraint; declare only the part that is always true (for example `Minimum = 1`) and enforce the
configured ceiling in the tool.

The runtime classifies its own failures (`ToolCallResult.FailureKind`): argument validation and
unknown tools are `Validation`, a timeout is `Timeout`, a thrown exception is `Internal`, and a
policy, approval, envelope or entitlement denial is `Authorization`. A tool sets `Environment`
when the host cannot provide what it needs (a provider is missing or unreachable, access is
denied, the operation is unsupported here) and may set `Validation` for its own defence-in-depth
checks. The model sees the kind in its observation (`ERROR (environment): ...`); a result with no
kind keeps the plain `ERROR: ...` wording.

`Success` does not mean the evidence is whole. A tool that can collect only part of what was asked
for sets `ToolCallResult.Completeness` to `Complete`, `Partial` or `Unavailable`; a tool that
declares nothing leaves `Unspecified`. Derive the value from the same fields you already emit
(`complete`, `truncated`, `partial`, `status`) so the two cannot disagree: `complete: true` is
`Complete`; a truncated result, a partial source or `partial: true` is `Partial`; no readable
source, or a subject that does not exist (a process that has exited, a root that was not found),
is `Unavailable` even when the output also says `partial: true`, because there is no evidence to
be partial about. A list cut by its `limit` is `Partial`; to know it was cut, count what you
observed before applying the limit. An empty result that answers the question fully is still
`Complete`. The runtime records both values on the step and in the audit record and never parses
your output to find them.

## Writing the entry type

```csharp
using bOps.Abstractions;

namespace Acme.SamplePlugin;

public sealed class SampleToolProvider : IToolProvider
{
    public IEnumerable<ITool> GetTools() => [new SampleEchoTool()];
}
```

The loader constructs this via `ActivatorUtilities`, against a container exposing *only*
`ILoggerFactory`, `IHttpClientFactory`, `TimeProvider`, your own `IConfigurationSection`
(bound from the host's `Plugins:<your-id>` configuration section) and `ICapabilityProbe`
(rule A10). Ask for anything else in your constructor and activation fails — that list changes
only by an ADR to this project, not by adding a dependency to your plugin.

## Writing a Skill

Implement `ISkillProvider` when package code must turn observed evidence into findings and an
immutable action plan. Each `ICapability` exposes a `CapabilityManifest` and implements
`PrepareAsync`. The host supplies `IToolInvoker` only for that invocation; it can call visible
`Read` tools belonging to the same package and still applies validation, policy, timeout, output
limits and audit. The invoker is not available in the provider constructor and cannot be used to
resolve registries or another package's services.

The sample provider exposes `sample.echo-marker-skill`. Its `sample.echo-marker` Capability reads
real evidence through `sample.echo`, creates a Finding that cites that Evidence, then returns a
one-step immutable plan using the safe `sample.marker.create` action. The action accepts a bounded
marker identity rather than a caller-controlled path and is independently verified by the
`sample.marker.status` Read tool.

Skill-originated calls fail closed unless `policy.yaml` contains exactly one matching contextual
rule. All six fields are required and matching is ordinal/exact—there are no V1.1 wildcards:

```yaml
skills:
  - skill: sample.echo-marker-skill
    capability: sample.echo-marker
    target: local
    environment: test
    blastRadius: single
    mode: automatic
```

For a non-Read Capability, preparation and execution are intentionally separate. Inspect the
prepared report and plan, approve the canonical plan hash, then execute that exact prepared run.
Per-step Tool policy and verification still apply. V1.1 Skill runs are terminal and non-resumable:
after interruption, prepare a new run and obtain a new approval rather than replaying a partial
plan.

## Signing, trusting and installing it locally

Build your plugin (`dotnet build -c Release`), then point the CLI at the output directory
containing your assemblies and `bops-plugin.json`. V1.0 uses a detached RSA-PSS/SHA-256
signature over a deterministic inventory of every package file. Generate and protect a publisher
key using your normal PKI process; this OpenSSL example is suitable for local development only:

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out acme-private.pem
openssl pkey -in acme-private.pem -pubout -out acme-public.pem
bops plugin sign ./bin/Release/net10.0/ Acme acme-2026 acme-private.pem
```

The operator—not the package—assigns trust. Add the public key to the configured
`Plugins:TrustStorePath` (by default `publisher-trust.json` beside the process working directory):

```json
[
  {
    "publisher": "Acme",
    "keyId": "acme-2026",
    "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----",
    "trust": "Verified"
  }
]
```

The `publisher` must exactly match both `bops-plugin.json` and the signature envelope. The key id
selects a specific rotation of that publisher's key. Then validate and install:

```bash
bops plugin validate ./bin/Release/net10.0/    # validates manifest, digest, signature and local trust
bops plugin install ./bin/Release/net10.0/     # re-verifies staged bytes and installs disabled
bops plugin list                               # confirm it is there
bops plugin enable acme.sample-plugin --confirm-version 1.0.0   # re-verifies installed bytes, then activates
```

There is no remote install and no auto-enable: a discovered plugin stays disabled until you
enable it explicitly (rule S8) and name the exact version that will execute in-process
(`--confirm-version`). `bops plugin` uses the same lifecycle service as the API (ADR-0037): install
admits a local directory or `.zip` only when it is signed by a locally trusted key, recovery runs
before every mutation, and a stale `--expected-version` is refused. Modification after signing or
installation invalidates its digest and blocks activation.

```bash
bops plugin disable acme.sample-plugin   # unload it; its files stay on disk
```

`bops plugin remove` is refused: ADR-0037 defines no removal transaction, and deleting a
generation the lifecycle metadata still names would leave it inconsistent.

## What isolation actually means here

`AssemblyLoadContext` isolation gives your plugin its own dependency resolution — it can bring
its own version of a NuGet package without colliding with the host's — but it is **not** a
security sandbox (rule S8). Your plugin runs with the host process's own privileges. Installing
a plugin is equivalent to installing software with those privileges. V1.0 signatures establish
publisher-key provenance and byte integrity; they do not review, constrain or sandbox the code.
The one thing genuinely shared is `bOps.Abstractions` itself: your
plugin never gets its own, second, incompatible copy of the contract types it talks to the host
through.
