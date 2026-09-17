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
