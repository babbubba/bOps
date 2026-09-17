# Handoff — V1.1-D complete; V1.1-E active

V1.1-A through V1.1-D are complete on public `bOps` `main`. V1.1-D adds governed permanent
recursive and batch deletion backed by a complete, expiring, instance-specific manifest. GitHub
Actions run `35230568622` passed on Windows and Linux after the Linux directory ctime behavior was
identified by CI, corrected and reproduced in a Linux SDK container.

The formal V1.0 release gate remains independently operator-gated. No tag, package publication,
release workflow or commercial-repository product change was made.

## V1.1-D delivered

- `fs.delete_tree.prepare` builds an exact task/actor/node-scoped manifest through the V1.1-C
  enumerator. Batch roots are explicit, policy-checked, deduplicated and overlap-reduced; globs and
  incomplete or over-limit enumeration fail closed.
- `fs.delete_tree` is a distinct High-risk, one-shot permanent operation. Its manifest requires
  human approval even under automatic policy, and `IApprovalBoundTool` atomically binds the exact
  manifest id/hash before execution.
- The instance-specific SHA-256 hash covers scope, semantics, roots, expiry, counts, bytes and every
  entry identity. The complete list remains in SQLite and never enters model or audit payloads.
- Execution performs a complete zero-delete reconciliation, then immediate policy/identity checks
  and non-recursive children-first deletes. Added, removed, renamed, replaced, symlinked or escaped
  entries fail closed; interruption and OS errors remain visible as partial completion.
- `fs.delete_tree.verify` checks every approved entry and classifies the result as verified,
  refuted or inconclusive. Per-entry outcomes remain pageable for recovery.
- Operator APIs provide scoped summary, cursor paging, search and streamed NDJSON download.
  Approver APIs resolve only through a live pending approval id.
- The Angular approval surface shows roots, warnings, counts, bytes, hash and expiry, keeps one
  bounded page in the DOM, invalidates stale/hash-mismatched previews and requires a separate
  permanent-deletion acknowledgement.
- Runtime argument validation now rejects unknown names and JSON-native type mismatches before
  policy. Optional 8 KiB audit summaries retain deletion hash/count/outcome evidence without
  copying the complete entry list.

See `docs/governed-recursive-deletion.md` and ADR-0027 for operator and normative details.

## Validation on 2026-09-17

- `dotnet restore bOps.slnx --locked-mode` — passed.
- `dotnet build bOps.slnx --configuration Release --no-restore` — 0 warnings, 0 errors.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"` — all
  executed assemblies passed locally. Relevant counts: Filesystem 66 passed/5 platform skips,
  API 20/20, Runtime 129/129 and Architecture 4/4.
- `npm run build` and `npm test -- --watch=false` — passed; Angular 18/18.
- Linux container deletion suite — 16/16, including symlink, permissions, case sensitivity, long
  path, concurrent mutation and the 10,000-file bounded paging scenario.
- GitHub Actions run `35230568622` — Windows and Ubuntu restore/build/non-live tests and Angular
  checks passed; Ubuntu also generated and uploaded SBOMs.

## Next action and boundaries

Implement `agentic/_tasks/2026-09-16-v1.1-e-web-capabilities.md` with effort **molto alto**. Start
with its required Web-package ADR and threat model: operator-configured SearXNG JSON search plus a
separate safe fetch with strict SSRF, redirect, DNS-rebinding, size, timeout and content controls.

Do not start V1.1-F or later work until V1.1-E closes. Do not create a release tag or publish
packages without separate operator authorization. The private commercial repository remains
product-gated and unchanged; only the workspace submodule pin advances after this public closure.
