# Suppressions

Every suppression beyond the shared list in `Directory.Build.props` is recorded here, per
[`agentic/02-coding-standards.md`](../../agentic/02-coding-standards.md): rule id, scope,
reason, and the condition under which it can be removed.

| Rule | Scope | Reason | Remove when |
|---|---|---|---|
| `CA1720` | `bOps.Abstractions.ToolParameterType` (enum) | The analyzer flags identifier names that contain a type name (`String`, `Integer`, `Boolean`). For a JSON-Schema-like parameter type enum these are exactly the correct, most legible names — renaming to dodge the rule (e.g. `Text`, `WholeNumber`) would make the contract worse to read. | Never — this is a permanent, considered suppression, not a placeholder. |
