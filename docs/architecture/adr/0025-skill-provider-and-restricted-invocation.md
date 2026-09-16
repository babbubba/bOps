# ADR-0025 — Skill providers, restricted evidence invocation and terminal runs

Status: Accepted

## Context

ADR-0023 defined Capability, Evidence, Finding, SkillReport and immutable ExecutionPlan values.
ADR-0024 proved that an ExecutionPlan can run through the existing policy, approval,
verification and audit path. V1.1-A still needs the executable package boundary that builds a
plan from real evidence without giving package code access to the host registry or service
provider.

The boundary must preserve four invariants:

- a package cannot impersonate another package;
- evidence collection can call only Read-risk tools owned by that same package;
- every evidence and plan tool call still crosses validation, policy, timeout and audit;
- approval applies to the exact immutable plan that later executes.

## Decision

### Public contracts

`bOps.Abstractions` adds these additive interfaces and records:

- `CapabilityRequest`: typed input plus target, environment, blast radius and dry-run intent;
- `ICapability`: exposes one `CapabilityManifest` and prepares a `SkillReport` from a request;
- `ISkillProvider`: exposes a stable Skill id, its deterministic Capabilities and its package
  tools; it extends `IToolProvider` so one activated package owns both the Skill and every tool
  the Skill may use while gathering evidence;
- `IToolInvoker`: one minimal asynchronous method accepting a tool name and `ToolArguments` and
  returning a `ToolCallResult`.

An `ICapability` receives `IToolInvoker` as an invocation-scoped argument. It never receives
`IToolRegistry`, `IServiceProvider`, policy, audit, approval or another package's services.
Package identity is not present in the public invocation method: the host binds it when it creates
the invoker, so package code has no value it can forge.

### Registry and activation

The node-scoped `ISkillRegistry` stores activated Skill providers. Registration stamps the host
assigned `PackageId` onto every Capability manifest, rejects blank or duplicate Skill/Capability
identities, rejects duplicate object identities returned by one provider, and preserves
deterministic ordinal ordering.

Dynamic activation accepts an `ISkillProvider` only after the existing signature and publisher
trust checks succeed. Its declared Capability names must exactly match the plugin manifest.
Disable/unload unregisters both tools and Skills before releasing the load context. A Skill
provider may also expose its tools because `ISkillProvider` extends `IToolProvider`; it may not
also be a model provider.

### Preparation and execution

`AgentRunner.PrepareSkillAsync` resolves the activated provider and Capability, creates a bound
`IToolInvoker`, and invokes deterministic package code under the Capability timeout. Every
`IToolInvoker` call:

1. resolves through the node's real tool registry;
2. rejects a missing, disabled, platform-incompatible, cross-package or non-Read tool;
3. runs through argument validation, contextual policy, timeout and audit;
4. returns a bounded `ToolCallResult` and never exposes the registry or concrete tool instance.

The prepared report is validated before it leaves the runtime: evidence and finding identifiers
must be unique, every Finding must cite recorded Evidence, and any plan must match the selected
Capability name/version. A plan whose step risk exceeds the Capability manifest is rejected.

`AgentRunner.ExecutePreparedSkillAsync` re-resolves the same activated Skill and Capability,
revalidates the prepared report and requires an approved `ExecutionPlanApproval` with an exact
hash for every non-Read Capability. It then executes the plan through ADR-0024's existing tool
pipeline and merges preparation and execution evidence into one report. Capability approval never
waives per-step policy, approval or verification.

### Contextual policy

`policy.yaml` gains an optional `skills` sequence. Each rule names an exact Skill, Capability,
target, environment, blast radius and mode. A Skill-originated call with missing context, no exact
matching rule or multiple exact matches is Forbidden. Critical remains unconditionally Forbidden
and package ceilings still apply before contextual rules. Plain tool calls retain the existing
tool/default behavior.

Exact matching is deliberate for V1.1: wildcard target or environment rules would introduce a
second pattern language at an authorization boundary. They can be added later by a separate ADR.

### Audit and correlation

Skill preparation and execution emit `SkillRunAuditEvent` records correlated by task id and run
id. Contextual tool and policy audit events carry Skill id, Capability name, target, environment,
blast radius and plan hash when present. Evidence collection therefore remains attributable
without placing evidence data or tool output in telemetry.

### Failure, cancellation and lifetime

Capability exceptions are translated to a failed, audited Skill report; expected tool failures
remain `ToolCallResult` values. Caller cancellation propagates. The Capability timeout is a hard
upper bound over preparation.

V1.1 Skill runs are deliberately terminal and non-resumable. They are not written to `ITaskStore`
and there is no Skill resume API. If preparation or execution is interrupted, the caller starts a
new run and obtains a new plan and approval. This is enforced by the API shape and documented
instead of pretending that replaying a partially executed plan is safe. Durable Skill-run state
and reconciliation require a future ADR before implementation.

## Alternatives considered

- Give Skills `IToolRegistry`. Rejected: it exposes cross-package tools and concrete instances,
  defeating A9/A10 and package identity binding.
- Inject a process-wide `IToolInvoker` into plugin constructors. Rejected: actor, task, Skill and
  Capability correlation are invocation-scoped, and a process-wide service would either lose
  that context or accept forgeable context parameters.
- Let one `ExecuteSkillAsync` prepare and immediately run. Rejected: an operator cannot approve
  an immutable hash until the plan exists and has been inspected.
- Persist only the plan index. Rejected: after side effects, an index does not prove which effects
  happened or whether they were independently verified.

## Consequences

- The public SDK remains additive and dependency-free.
- Runtime construction gains a node-scoped Skill registry.
- Plugin activation can register a combined Skill/tool provider through the existing trusted
  boundary.
- SafeDefault contains no contextual Skill grants; Skill calls fail closed until an operator or
  host supplies an exact Skill policy rule.
- V1.1 proves the complete boundary with one signed, dynamically loaded sample Skill. It does not
  claim crash resumability; that remains an explicit future design problem.
