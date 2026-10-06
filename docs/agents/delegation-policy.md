# Configuring delegation in `policy.yaml`

Delegation is **off until you grant it**, and bOps never grants it by itself: no profile is created at startup, by the API, by
the UI or by any other command than the one you run below. The built-in default policy has no role profiles, so `bops delegate`
and `POST /api/delegations` end `Denied` on the `Profile` dimension before any model call. You turn it on, role by role, with an
optional `delegation` section (ADR-0030, ADR-0031, ADR-0044). The model behind it is in [delegation.md](delegation.md).

## Which roles a delegation needs

What a delegation needs depends on what it asks for (ADR-0044):

| Request | Required roles | Not required |
|---|---|---|
| A diagnosis (no change prepared) | Discovery, Diagnostic | Remediation, Verification |
| A remediation (a change prepared) | Discovery, Diagnostic, Remediation, Verification | — |

A required role without a usable profile denies the delegation before any model call, naming the role. A role that is not
required is never consulted: its profile, present or absent, grants nothing to a diagnosis. A diagnosis can only ever reach
`Read` tools: Discovery and Diagnostic are capped at `Read` whatever the file says, and a diagnosis has no Skill or Capability.

## A fresh install: the safe path to a diagnosis

1. **Look first.** `bops delegate readiness` shows every role and why it is or is not usable; on a fresh install Discovery and
   Diagnostic are `missing` and the command exits `2`.
2. **Generate read-only profiles for review.** `bops delegate profiles init --read-only` prints, and writes nothing, a whole
   `policy.yaml` with Discovery and Diagnostic profiles that list every `Read` tool available on this host by exact name, with
   finite budgets (15 steps, 150,000 tokens and 30 minutes per role), `maxRisk: read`, `maxBlastRadius: single`,
   `targets: [local]` and `environments: [local]`. It never writes a Remediation or Verification profile, a Skill, a Capability
   or anything above `Read`. Standard error names the absolute path it would write.
3. **Write it.** `bops delegate profiles init --read-only --write` writes that file atomically to the CLI's `Policy:FilePath`
   (the `Policy__FilePath` environment variable or `appsettings.json`; default `policy.yaml` in the working directory). An
   existing file is never overwritten silently: the command refuses and prints the `delegation` fragment for you to merge by hand.
4. **Restart the API.** `bOps.Api` reads `policy.yaml` only at start, from its own `Policy:FilePath`, which must name the same
   file ([operator-configuration.md](../operator-configuration.md)).
5. **Check.** `bops delegate profiles check` (exit `0` when nothing drifted) and `bops delegate readiness` (Discovery and
   Diagnostic `ready`, exit `0`). The Delegations page shows the same readiness before you submit.
6. **Diagnose.** `bops delegate "why is web-1 slow?"`, or start it from the UI. It ends `DiagnosisCompleted`.

`bops delegate readiness --remediation` stays `2` (Remediation and Verification `missing`): preparing a change still needs all
four profiles, which you write yourself, as in the example below.

**Why the generated file has `defaults`.** A `policy.yaml` without a `defaults` section forbids every risk level. The generated
file therefore repeats the built-in default (`read`/`low` automatic, `medium`/`high` approval, `critical` forbidden), so creating
it changes no decision outside delegation.

**Regenerating after a tool is installed.** Tools are listed by exact name, expanded when the file is generated; a `Read` tool
installed later is not included (there are no wildcards evaluated at runtime). `profiles check` reports it as drift. To refresh
the tool list run `bops delegate profiles init --read-only --write --overwrite`. `--overwrite` replaces the file only if it is
still exactly what the generator writes apart from the tool list: it loads; its `defaults` are the built-in ones with no `tools`,
`packages` or `skills` entries; it has no Remediation or Verification profile; its Discovery and Diagnostic profiles equal the
generated ones in every dimension except `tools`; it has not changed since it was read. Otherwise it refuses, names the first
failed condition, leaves the file untouched and prints the generated content for a manual merge. Comments in the replaced file
are lost, and the file keeps its permissions.

## Readiness and drift

- **Readiness** is what the runtime would decide for a request of each shape, computed by the same reducer that runs a delegation
  — never a second implementation. Per role it is `ready`, `missing` (no profile, or the policy failed to load), `malformed`
  ("Not usable": present, but the reducer refuses it, naming the dimension) or `notRequired`. `ready` means the required role
  profiles are usable for this type of delegation; it is **not** a promise that a particular change will run — the selected
  Skill, Capability, target, environment and input are validated when you submit.
- **Drift** is information and never changes readiness: an available `Read` tool missing from a Discovery or Diagnostic profile
  (`read_tool_not_granted`), a listed tool that is not available on this host (`unavailable_tool`), a listed tool above the
  role's cap (`above_role_risk_cap`), a configured profile the reducer refuses (`unusable_profile`) or a policy that failed to
  load (`policy_load_failed`). `bops delegate profiles check` lists the items and exits `0` with none, `2` if any blocks
  delegation for some request shape, `8` if all are informational. The API reports the count as `profileDriftCount`.

## A working example

This is the whole section for a host that lets one service be diagnosed and restarted. It is loaded by a test, so it stays valid.
A file needs a `defaults` section as well (a file without one forbids every risk level); copy the one the generated file has.

```yaml
delegation:
  roles:
    discovery:
      tools: [system.info, service.status]
      maxRisk: read
      maxBlastRadius: single
      targets: [web-1]
      environments: [prod]
      maxSteps: 12
      maxTokens: 20000
      maxDuration: 00:10:00
    diagnostic:
      skills: [service.skill]
      capabilities: [service.restore]
      tools: [system.info, service.status]
      maxRisk: read
      maxBlastRadius: single
      targets: [web-1]
      environments: [prod]
      maxSteps: 12
      maxTokens: 20000
      maxDuration: 00:10:00
    remediation:
      skills: [service.skill]
      capabilities: [service.restore]
      tools: [service.restart]
      maxRisk: high
      maxBlastRadius: single
      targets: [web-1]
      environments: [prod]
      maxSteps: 5
      maxDuration: 00:05:00
      window:
        start: 2026-09-20T02:00:00Z
        end: 2026-09-20T04:00:00+00:00
    verification:
      tools: [service.status]
      maxRisk: read
      maxBlastRadius: single
      targets: [web-1]
      environments: [prod]
      maxSteps: 5
      maxDuration: 00:05:00
```

## The rules

- **Roles are optional, and a role with no entry has no profile.** A delegation is refused when a role it requires has none
  (Discovery and Diagnostic for a diagnosis, all four for a remediation).
- **Nothing is granted by leaving it out.** An absent list is empty, absent `maxSteps` and `maxTokens` are 0, an absent `window` means no
  restriction beyond the deadline.
- **`maxRisk`, `maxBlastRadius` and `maxDuration` are required.** A ceiling always permits its lowest value and a duration must be
  positive, so there is no "nothing" to default to. `maxRisk` is `read`, `low`, `medium`, `high` or `critical` (`critical` still
  never runs); `maxBlastRadius` is `single`, `multiple` or `fleet`. Names only: `3` and `low, medium` are refused.
- **Lists are exact names.** `tools`, `skills`, `capabilities`, `targets` and `environments` take exact values, no wildcards. A blank
  value (`tools:`) is an error and `[]` says none, because a blank could be "forgot to fill in".
- **`maxDuration` is `hh:mm:ss` or `d.hh:mm:ss`.** `00:10:00` is ten minutes, `1.02:00:00` is a day and two hours. A bare `10` is refused,
  not read as ten days.
- **A window instant needs a zone.** `Z` or an offset such as `+02:00`. An instant without one means a different moment on every
  machine, so it is refused. If a profile's window, the request's and the parent's do not overlap, the delegation is denied.
- **Each role has dimensions it may not be given.** `skills` and `capabilities` are refused for `discovery` and `verification`;
  `maxTokens` above 0 is refused for `remediation` and `verification` (they make no model call). Refused means the section is malformed,
  not ignored.
- **Unknown keys are errors** inside `delegation`, at every level, and the message names the line and the path. A misspelt section
  name itself (`delegaton:`) is not seen at all and configures nothing, like any unknown top-level key.
- **A malformed section makes the whole policy `AllForbidden`.** The hosts fall back to the state in which every tool above `Read` is
  forbidden and no role has a profile, and log why. Fix the file and restart. It is deliberate: a half-loaded policy would be more
  permissive than what you wrote.
- **A zero required budget is well-formed** but is a denial when a delegation starts, not a load error.

## Checking it

Run `bops delegate readiness` (add `--remediation` for the change shape) before starting anything: it prints the four roles with
the reducer's reason and dimension, whether the shape is ready, the policy path with its load state (`noFile`, `loaded`,
`loadFailed`) and the drift count. It exits `0` when ready and `2` when not. `bops delegate profiles check` lists drift. The API
answers the same question at `GET /api/delegations/readiness` ([delegations-api.md](delegations-api.md)), and the Delegations page
shows it next to the start form.

If a delegation still ends `Denied`, `Refused on Profile` names the role whose profile is missing or not usable; `bops` and the
API also log why a `policy.yaml` failed to load (the API never returns the loader's message over HTTP). Envelope refusals during
a run are audited as `Forbidden` policy decisions with a reason that starts with "Authority envelope:".
