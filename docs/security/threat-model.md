# bOps threat model

## Scope and security objective

bOps turns non-deterministic model proposals into typed operations on a live Windows or Linux
machine. Its security objective is not to make an LLM trustworthy. It is to ensure that model
output is treated as an untrusted proposal, that a local policy and authenticated operator decide
whether an action may execute, that side effects are verified, and that every outcome is recorded.

This model covers the V1.0 local runtime, CLI, HTTP API, Angular UI, local task/audit stores,
dynamic in-process plugins, provider HTTP calls and release supply chain. Future node-to-Control
Plane transport is a separate boundary and is not implemented in V1.0.

## Assets

- Integrity and availability of the managed host.
- Operator and approver identities and authorization decisions.
- Model/API credentials and any future tool secrets.
- Task goals, plans, tool arguments/results and audit history.
- Plugin bytes, publisher provenance and local enablement decisions.
- The integrity and reproducibility of released SDK/runtime artifacts.

## Trust boundaries

1. **Operator to CLI/API.** CLI identity comes from the local OS account. API identity comes from
   a bearer credential resolved by the host; no client-supplied display field is identity.
2. **Runtime to LLM provider.** Prompts and tool schemas leave the machine. Secrets never enter
   prompt history. Provider responses are untrusted protocol data.
3. **Runtime to tool package.** The runtime validates arguments and policy first, but an in-process
   package has the host process's full OS privileges.
4. **Tool output to model.** Output may contain prompt injection. It enters only a structured,
   delimited tool-result turn and cannot mutate runtime policy, tools or budget.
5. **Host to local storage.** Task, plugin and audit files are node-local. Atomic writes and
   restrictive permissions reduce accidental exposure; the audit hash chain detects historical
   edits but cannot stop a writer who can recompute the chain.
6. **Publisher to plugin loader.** A detached package signature proves byte integrity and control
   of a locally trusted key. It does not make code safe.
7. **Source to release artifact.** CI builds on both supported OS families and emits checksums,
   SBOM and platform provenance for the exact artifacts it publishes.

## Threats and controls

### Malicious or mistaken model output

The model cannot execute code or mutate runtime state. It can only request registered typed tools.
Manifest validation rejects unknown or malformed arguments. Policy fails closed, `Critical` is
unbypassably forbidden, approval-gated actions require an authenticated approver, every action has
a timeout, and every non-read action has package-owned post-action verification.

Residual risk: an allowed typed action can still be operationally harmful. Operators must keep
policy narrow and run the host with least privilege.

### Prompt injection through tool output

External text is observational data. Delimiters are generated and neutralized, tool results use
the native tool-result role, and the standing system prompt says output cannot change goals,
policy, tools or operator intent. A poisoned result may influence the model's next proposal, but
that proposal is still independently validated, authorized and audited.

### API spoofing and privilege escalation

All `/api` operations require an authenticated bearer key. Read endpoints require `viewer`, task
mutation requires `operator`, and approval decisions require `approver`. Keys are compared in
constant time, never accepted in a URL, and configured by secret reference. `ActorIdentity` is
derived from claims established by the authentication handler. Rate and concurrency limits bound
abuse; idempotency keys prevent duplicate task creation during safe retries.

Residual risk: bearer keys are replayable while valid. V1.0 is local-only and expects TLS or a
loopback/reverse-proxy boundary. Rotation means replacing the referenced secret and restarting the
host. Remote multi-tenant identity belongs to the later Control Plane milestone.

### Secret disclosure

Committed configuration stores only `SecretReference` provider/name pairs. The environment
provider resolves exact variable names at the host boundary. Resolved values are used only to
construct authorization headers or compare API credentials; they are never included in prompts,
DTOs, audit events, logs, telemetry, exception messages or reports. Provider-status output reports
presence, not value.

Residual risk: environment variables and process memory are readable by sufficiently privileged
local actors. bOps cannot protect secrets from an administrator or debugger with access to its
process. Run under a dedicated least-privilege account.

### Malicious plugin or dependency

New plugins install disabled. The loader inventories every file, verifies a detached signature,
looks up the publisher/key in a local trust store, and records provenance. Enablement fails closed
for unsigned, modified, unknown-key or invalidly-signed packages. Package ids are assigned by the
host and policy applies a local trust ceiling.

Installing a plugin is equivalent to installing software with the host's privileges. An
`AssemblyLoadContext` isolates dependencies, not permissions. Trust levels and signatures protect
against mistakes, tampering and publisher impersonation; they do not protect against malicious
code from a trusted publisher. Real isolation requires out-of-process execution and is not claimed
by V1.0.

### Filesystem traversal and time-of-check/time-of-use

Filesystem packages resolve paths, symlinks and `..` immediately before use and deny paths that do
not match configured patterns. Plugin installation stages into a host-owned directory, rejects
duplicate/untracked destinations and verifies the staged bytes before activation.

Exact filesystem inventories are stored outside tool output in an expiring SQLite database. Their
opaque ids are bound to node, task and actor; an incomplete, over-limit, inaccessible or changing
tree never produces an approval-ready reference. Symbolic links and reparse points are recorded
but not followed. The inventory content hash is evidence of one observed metadata set, not a lock:
any later destructive workflow must re-resolve policy and compare entry metadata immediately
before acting.

Permanent recursive deletion uses a separate High-risk `fs.delete_tree` tool that cannot accept a
path directly and cannot be configured to skip human approval. Its one-shot approval is bound to
an expiring, instance-specific hash of the complete exact set. The executor performs a zero-delete
full-set reconciliation, then a second identity and policy check immediately before each
children-first non-recursive delete. Added, removed, renamed, replaced, retyped or escaped entries
fail closed. Partial completion and verification are durable and explicit; neither atomicity nor
rollback is claimed. Audit retains bounded aggregate metadata and a scoped manifest reference,
never the complete path list.

Residual risk: platform filesystem semantics can still race an attacker with equivalent write
access between the final identity check and the operating-system call. Protect runtime directories
with OS permissions, keep the manifest database outside administered roots and do not share their
ownership.

### Outbound network requests (SSRF and DNS rebinding)

`web.fetch` accepts a model-chosen URL — adversarial input in the same sense as any other tool
output the model has previously seen, including a URL discovered inside a `web.search` result or a
prior fetch. Its `HttpClient` resolves and validates the destination address inside
`SocketsHttpHandler.ConnectCallback`, immediately before connecting, rather than at any earlier
point: loopback, link-local (including the cloud-metadata address), RFC 1918 private ranges, IPv6
unique-local, multicast and unspecified addresses are denied by default, and only a narrow explicit
operator allowlist overrides this. Because validation happens inside the same callback that performs
the connection, there is no window between checking a resolved address and using it — the address
validated is the address connected to. Redirects are followed by the package's own bounded loop, not
by the HTTP client, so every redirect hop re-enters the same validated connect path and a scheme
downgrade is rejected by default.

`web.search` calls only one fixed, operator-configured SearXNG endpoint — the same trust level as
the configured LLM provider or Docker endpoint elsewhere in this host — and does not carry
model-chosen input in the request target, so it is outside this control's scope.

Response handling bounds resource cost independently of destination trust: automatic decompression
is disabled so decompressed bytes can be counted and capped directly (defending against
decompression bombs), raw response size is independently capped, only an allowlisted set of textual
content types is decoded, and charset selection trusts only a small, unambiguous allowlist before
falling back to UTF-8 with a replacement decoder. See ADR-0028 and
[`web-network-policy.md`](web-network-policy.md).

Residual risk: an operator-added allowlist entry is trusted as intended; an operator who allowlists
a sensitive internal host accepts that risk explicitly. A destination that is publicly routable but
still undesirable (e.g. an unrelated third party) is not blocked — this control defends the node and
its private network, not general acceptable-use policy over fetched content.

### Audit deletion or rewriting

Each JSONL event is chained to the previous hash. The CLI verifier recomputes the entire chain and
reports the first broken sequence. Files are created with restrictive local permissions where the
OS exposes a portable primitive.

The chain is tamper-evident, not tamper-proof. Anyone able to replace the entire file can recompute
all hashes. Ship or attest audit roots outside the node when a later remote architecture exists.

### Crash, retry and duplicate execution

Task state is atomically persisted after each completed step. A process crash leaves a `Running`
task that an operator can explicitly resume. The resume path reconstructs model history from
persisted steps. In-flight approvals are discarded and requested again. API task creation accepts
an actor-scoped idempotency key, and only bounded transient provider failures are retried.

Residual risk: a process can die after a side effect but before its state write. Side-effecting
tools have independent verification, and operators must inspect/verify state before resuming.

### Delegated multi-agent runs (V1.2)

A delegated run (ADR-0030) takes an objective through Discovery, Diagnostic, Remediation and Verification. It adds an HTTP surface
(`/api/delegations`, see `docs/agents/delegations-api.md`) and a flow of data between roles. Both are new attack surface.

**Who may do what.** Viewing needs `viewer`; starting, cancelling and resuming need `operator`; deciding a plan needs `approver`;
settling a step whose outcome is not known needs `administrator`. The role is checked before the handler runs, and the person who
decides is always the authenticated principal: no request body names an approver, a canceller or a reconciler. The runtime refuses a
decision whose identity is an agent, the runtime itself or one of the run's own agent ids, and audits the refusal. A principal
holding both `operator` and `approver` can approve the plan of a run it started; a second person (four eyes) is not required in
V1.2, so grant the two roles to different keys where that matters.

**Approval is bound to the plan.** A decision carries the plan hash it is about. The queue refuses one for another hash, the runtime
binds the approval to the hash again, and a plan that changes is a new hash and a new request. Approvals are memory-only and are
asked again on resume, for the same reasons as in the approval lifecycle decision below. A run started with a `policy.yaml` that failed to load, or
with no `delegation` section, has no role profile and ends `Denied` before any model call.

**Inter-agent data flow.** Roles do not talk to each other and share no conversation. Discovery gives Diagnostic structured
evidence, each piece marked with the delegation, agent and role that produced it; Diagnostic gives Remediation findings that cite
evidence ids and a plan; Verification is given only the approved plan and reads the system through its own reduced authority, with
no model. What one role reads reaches the next only as data in a delimited tool-result turn, never as instruction, and cannot widen
an envelope, which is computed by the runtime from the operator's request and the profile and can only be narrowed at each step.
Poisoned tool output can therefore shape a finding and a proposed plan, but the human sees the plan, its steps and arguments, the
findings and the evidence they cite, and the authority it would run under before approving, and a change is confirmed by a role that
did not make it.

**What the API discloses.** A run's view omits the data of each piece of evidence (what tools returned), the full authority of each
role and the model requests and replies; it carries ids, descriptions, statuses, hashes, bounded error text and who decided.
Telemetry never carries output or arguments. The pending-approval list shows the plan's own arguments, as the existing approvals
queue does, so treat the `approver` role as able to read them.

**Crash, cancel and reconcile.** Every side-effecting step is journaled before it runs and settled after. A step whose outcome is
unknown is settled by its own verification or waits for an `administrator`, and is never retried. Cancelling a run this host is
executing stops it in place and is audited under the operator who asked; cancelling a stored run a crash left `Running` closes it
in the store.

**Denial of service.** The host caps concurrent delegated runs (503 beyond it), a start waits only until its run is stored, and
every role has a step, token and deadline budget.

Residual risk: no four-eyes rule; an `approver` can read plan arguments; an in-process package still has the host's privileges (S8).

### Denial of service

API rate limits, maximum concurrent agent runs, model/tool timeouts, step/replan/token/cost budgets,
bounded history and output truncation limit resource use. Cancellation is propagated. SQLite uses
WAL and a busy timeout for concurrent readers/writers.

Residual risk: a local user with process or disk control can still starve the service. bOps is not
an OS-level resource sandbox.

### Release and dependency supply chain

CI restores from lockable manifests, builds and tests on Windows/Linux, generates SBOMs and
third-party notices, publishes checksums and attaches build provenance to release artifacts.
Dependency scanning is a release gate. Reproducibility is checked by publishing the same source
twice and comparing normalized artifact hashes.

Residual risk: upstream package registries and CI actions remain dependencies. Pin actions to
reviewed immutable revisions for high-assurance deployments and review SBOM changes.

## Approval lifecycle decision

Pending approvals are intentionally memory-only. After a crash there is no authenticated HTTP
request waiting for the answer, the approver's authorization may have changed, and the target may
have drifted. A task remains resumable, but the resumed execution must reach policy again and ask a
currently authenticated approver. This is safer than making an old approval replayable.

## Deployment assumptions

- Bind the API to loopback unless it is behind TLS and an authenticated local reverse proxy.
- Run under a dedicated account with access only to approved paths/services.
- Keep runtime state and publisher trust-store files owned by that account.
- Use separate read-only and action-capable instances/accounts when operationally possible.
- Never run routinely as root or LocalSystem.

## Out of scope

- Protection from an administrator, kernel compromise or debugger attached to the process.
- Sandboxing malicious in-process plugins.
- Remote node transport, multi-tenant authorization, entitlement or commercial Skills.
- Generic shell/process/SQL execution; these remain permanently prohibited.
