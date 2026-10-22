# Provider adapter serialization and service tools — technical reference

> **Technical reference, not a decision register.** Small implementation specifications that no existing guide carries, extracted from ADR-0038 §2 and
> ADR-0021 during the architecture consolidation (DOC-02). The binding rules are in
> [`../current/01-maf-agent-infrastructure.md`](../current/01-maf-agent-infrastructure.md) (C01-R10) and
> [`../current/02-tools-safety-authority.md`](../current/02-tools-safety-authority.md) (C02-R19). If this page and a contract disagree, the contract wins.

## 1. Provider request serialization: null omission (ADR-0038 §2)

`OpenAiJsonContext` and the Anthropic serializer context use `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` (the Anthropic request DTOs would otherwise emit
`null` for the unused members of a content block). Requests therefore omit `content`, `tool_calls`, `tool_call_id`, `tools`, `tool_choice` and the unused block members whenever
they are semantically absent; this matches the OpenAI contract, where `content` is required only when the message has no `tool_calls`. The setting is scoped to those two serializer
contexts, so no other JSON in the product changes.

## 2. Service tools (ADR-0021)

- **Windows:** `System.ServiceProcess.ServiceController` — `GetServices()` for the list and `new ServiceController(name).Status` for one service (a Microsoft MIT-licensed package
  reference; SBOM and third-party notices are regenerated with the code).
- **Linux:** fixed `systemctl` invocations only (see C02-R19): `list-units --type=service --all --no-legend --no-pager --plain` and `show <name>
  --property=LoadState,ActiveState,SubState,Description --no-pager`, parsed from `Key=Value` lines.
- `service.list` / `service.status` report a status **normalized** to `running` / `stopped` / `failed` / `unknown` on both platforms (two OS packages producing the same tool
  produce the same shape; the Windows `ServiceControllerStatus` and systemd `ActiveState` vocabularies are never exposed to the model).
- An absent unit name and a syntactically implausible one are reported identically (`exists: false`), so a raw `systemctl` parse error never reaches the model.
- `service.status` returns the structured shape `name`, `exists`, `status`, `description` (the same kind of parseable output as `fs.stat`), designed as the verification target for any
  later service-control tools.
- Migrating the Linux implementation to D-Bus would be confined to `bOps.Packages.Service.Linux`'s internals (the manifest, `Service.Core` shapes and every other package are
  unaffected). Service start/stop/restart tools are a materially different risk profile (non-`Read`, verification, policy and approval) and are outside this reference.
