# Configuring delegation in `policy.yaml`

Delegation is **off until you grant it**. The built-in default policy has no role profiles, so `bops delegate` and
`POST /api/delegations` end `Denied` on the `Profile` dimension before any model call. You turn it on, role by role, with an
optional `delegation` section (ADR-0030, ADR-0031). The model behind it is in [delegation.md](delegation.md).

## A working example

This is the whole section for a host that lets one service be diagnosed and restarted. It is loaded by a test, so it stays valid.

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

- **Roles are optional, and a role with no entry has no profile.** A delegation needs all four; without one it is refused.
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

Start a diagnosis (`bops delegate "check web-1"`). If it ends `Denied`, `Refused on Profile` says the file is missing or has no
profile for a role; `bops` also logs why a `policy.yaml` failed to load. Envelope refusals during a run are audited as `Forbidden`
policy decisions with a reason that starts with "Authority envelope:".
