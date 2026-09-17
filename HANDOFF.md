# Handoff — V1.1-A complete; V1.1-B active

V1.1-A implements the executable public Skill/Capability boundary described by ADR-0025. The work
was pushed to public `bOps` `main`, and GitHub Actions run `35178863698` passed on Windows and Linux.
V1.1-A is complete; V1.1-B system applications, devices and hardware-model inventory is active.

The formal V1.0 release gate is independent and remains operator-gated: no release-candidate tag
or authoritative release workflow run was created by this work.

## Delivered

- `ICapability`, `ISkillProvider`, `IToolInvoker`, `ISkillRegistry`, `CapabilityRequest`,
  `SkillDescriptor` and `PreparedSkillRun` extend `bOps.Abstractions` additively.
- Capability and Tool package identities are stamped by the host. The Skill registry rejects
  duplicate or malformed Skill/Capability identities and unregisters providers before unload.
- `AgentRunner.PrepareSkillAsync` runs deterministic Capability code under its declared timeout.
  Its invocation-scoped invoker permits only visible, same-package, `Read` tools and still applies
  argument validation, exact contextual policy, timeout, result bounds and audit.
- Prepared reports are validated: evidence/finding ids are unique, every Finding cites recorded
  Evidence, plans match Capability identity/version, and plan risk cannot exceed the manifest.
- `AgentRunner.ExecutePreparedSkillAsync` requires an affirmative exact-hash approval for every
  non-Read Capability, then reuses the normal Tool policy, approval, execution, verification and
  audit pipeline for each plan step.
- `policy.yaml` accepts exact Skill rules over Skill, Capability, target, environment, blast radius
  and mode. Missing, incomplete, duplicate or unmatched Skill context fails closed; Critical and
  package-ceiling rules remain stronger.
- Skill runs and their Tool/policy/approval events carry correlation context without recording
  evidence or Tool output in telemetry.
- V1.1 Skill runs are explicitly terminal and non-resumable. They write no Skill state to
  `ITaskStore`; interruption requires a fresh plan and approval.
- The signed sample plugin is now a real `ISkillProvider`. Its harmless echo-marker Capability
  gathers real evidence, produces a cited Finding and immutable plan, executes one bounded Low-risk
  marker action, and independently verifies it through a Read tool.

## Architecture and commits

- `03288c0` — ADR-0025: Skill provider, restricted invocation and terminal-run decisions.
- `822b0ff` — public SDK contracts and node-scoped Skill registry.
- `215958d` — exact contextual Skill policy and fail-closed parsing/evaluation.
- `a8862c9` — trusted plugin activation, preparation/execution runtime, audit and real sample Skill.
- `7164c3b` — caller-cancellation and platform-visibility negative coverage.
- The closure documentation commit aligns SDK/plugin guidance, roadmap, changelog and this handoff.

ADR-0023 and ADR-0024 remain accepted history. ADR-0025 is normative for the new executable
boundary. Decision D-020 mirrors it in the compact decision register. Rule A10 now correctly says
that `IToolInvoker` is invocation-scoped, not constructor-injected.

## Local validation on 2026-09-17

- `dotnet restore bOps.slnx --locked-mode` — passed.
- `dotnet build bOps.slnx --configuration Release --no-restore` — 0 warnings, 0 errors.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"` — all
  executed assemblies passed. Relevant counts include Runtime 125/125, PluginHost 48/48, Policy
  23/23, API 17/17, Audit 9/9 and Windows System 20/20. Existing platform-inapplicable tests were
  skipped by their guards.
- Targeted Skill execution tests — 10/10, including caller cancellation and platform-incompatible
  evidence tools.
- `bOps.Abstractions` packed successfully as `1.1.0-preview.1` with symbols.
- `git diff --check` passed; no generated SDK-validation artifacts are tracked.

No Angular source, API endpoint contract or browser behavior changed in this batch, so the Angular
build/test matrix was not rerun locally. The repository CI remains authoritative for both OSes and
its existing UI checks.

## Next action and boundaries

Implement `agentic/_tasks/2026-09-16-v1.1-b-system-inventory.md` with effort **alto**. Preserve the
existing System Core/Windows/Linux package pattern, define bounded common output shapes first and
prove them through conformance plus real-platform tests.

Do not create a tag, publish packages, pin the workspace submodule, or begin private commercial
work as part of V1.1-B unless separately authorized. The workspace root will correctly
show `repos/bOps` at a newer local commit until the submodule commit is reviewed/pushed and the
root pin is intentionally updated.
