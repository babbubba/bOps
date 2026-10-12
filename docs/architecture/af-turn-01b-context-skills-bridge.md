# AF-TURN-01B — Governed context provider and progressive-skills bridge

Base: AF-CONTEXT-01 `134f5ff`, AF-TURN-01A `efdd4b7`. ADR-0054 §§4–6 (Proposed). Not composed by any production host.

## What exists

- `BopsContextProvider` / `MafContextComposition` (`bOps.AgentFramework`): the one MAF `AIContextProvider` bOps admits. It is built per
  call from the `ModelContextRequest` the runner already produced and supplies the protected instructions, the protected-before and
  protected-after turns; the agent input is the historical turns. No prompt is generated in the provider. `MafGovernedTurnBroker`
  takes `MafContextAssembly.BopsProvider` from the host; the default is the 01A shape.
- `ContextGate` / `GovernedChatClient` take the host-assigned id of the composed provider. A message is admitted *for comparison* only
  when it is tagged AIContextProvider with that id; it must still equal the next bOps-projected turn. The id is fresh per call.
- `KnowledgeReadBridge`: public bOps tool `runtime.knowledge_read` (operations `discover`, `load`, `read_resource`). bOps invokes the
  framework's `load_skill` / `read_skill_resource` itself, from an immutable fixture pack and an explicit `ExperimentalProviderAdmission`.
  The framework function names are never declared to the model; `run_skill_script` has no entry. Each read is checked against the
  pin taken at task start and audited (`KnowledgeReadAuditEvent`: name and hash, never content).

## Divergences recorded (not loosened)

- The runner does not label the plan, the step and the evidence digest separately; they are the protected-before / protected-after turns
  and the history. The provider supplies those two groups as the runner split them. A finer split needs runner changes (out of scope).
- Provenance is unchanged on the broker request (no `AdmittedProviderId` on runtime content) to keep the request byte-equal to the
  current path; `Skill` provenance (pack id, version, manifest hash, host provider id) is set only on served knowledge content.
- The runner does not offer or serve `runtime.knowledge_read` (it answers it as an unsupported runtime function). Wiring it into
  `AgentRunner` is KNOW-01 work and needs its own review of step authority.
- Fixture hashes pin a bOps-owned in-memory pack against alteration. They are not a signed pack: no manifest signature, publisher trust
  or registry exists.
- Pins are taken over what the framework functions return, so a MAF upgrade that changes their formatting changes the pins (re-run the
  tests on upgrade, as ADR-0054 §7 already requires).

## G2 matrix

| Req | Status after 01B |
|---|---|
| G2-1 final context check | Closed (01A). Now also covers an admitted provider: forged id, other provider, System elevation, extra instructions, stale id refused. |
| G2-2 typed provenance | Closed for bOps content (01A) and for served skill content (`Skill`, pack id, version, hash, host provider id; audit counts). Pack-claimed ids and metadata ignored (tested). |
| G2-3 host-assigned trust | **Open for any external provider.** Only the bOps-authored provider and the test-admitted bridge exist; admission is a host-constructed object, not data. No decorator for third-party providers yet. |
| G2-4 no elevation | Partly closed for the bridge: content is a Tool turn, delimited, never System; bOps owns the catalogue template (no script text); names and descriptions validated and capped. Open for any other provider. |
| G2-5 tenant and cache isolation | **Open for external providers.** The bridge is per task with its own source and pin, no static state, tested under concurrency for two packs. No caching source and no tenant filter exist yet. |
| G2-6 architecture tests | Extended: single `AIContextProvider` subclass, providers only from the composition, framework skills provider only in the bridge, nothing composed by a production host, no configuration key, bridge has no model/agent/loop/script runner. |
