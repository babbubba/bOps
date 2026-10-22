# Claude Code skills for bOps development

Third-party Claude Code skills can help contributors who use Claude Code, but they are **development aids only**. They are
not part of bOps, are not shipped with it, and are **not copied into this repository**: each contributor installs them at user
scope from the original source, which keeps the upstream license and attribution intact.

Skills written specifically for bOps are different: they may be versioned normally under `.claude/skills/<name>/`. Only the two
third-party skills below are listed in `.gitignore`, so an accidental `git add` cannot publish them.

## Recommended external skills

| Skill | Source | License | Use in bOps |
|---|---|---|---|
| `microsoft-agent-framework` | [`github/awesome-copilot`](https://github.com/github/awesome-copilot/tree/main/skills/microsoft-agent-framework), `skills/microsoft-agent-framework` | MIT, Copyright GitHub, Inc. | Orientation on Microsoft Agent Framework APIs and documentation (bOps pins `Microsoft.Agents.AI`) |
| `prompt-engineer` | [`Jeffallan/claude-skills`](https://github.com/Jeffallan/claude-skills/tree/main/skills/prompt-engineer), `skills/prompt-engineer` (version 1.2.0 when this page was written) | MIT, Copyright (c) 2025 (the upstream license text names no holder; the skill metadata names Jeffallan and Synergetic Solutions) | Ideas for prompt design, structured outputs and evaluation |

Both licenses are MIT and require the copyright and permission notice to travel with any copy. Installing at user scope from the
upstream repository satisfies that, because the notice stays in the upstream repository you installed from. Do not redistribute
these files in bOps without the upstream `LICENSE`, an attribution and an entry in `THIRD-PARTY-NOTICES`.

## Install (user scope)

Requires the GitHub CLI 2.90.0 or later with the `gh skill` command (preview; behavior may change). The commands below were checked
against `gh skill install --help` and `gh skill preview` for both repositories.

```bash
gh skill install github/awesome-copilot microsoft-agent-framework --agent claude-code --scope user
gh skill install Jeffallan/claude-skills prompt-engineer --agent claude-code --scope user
```

Skills land in your home directory (`~/.claude/skills/`), available in every project and invisible to Git in this repository.
To pin a version for reproducibility, add `--pin <tag-or-commit>`. Update later with `gh skill update`.

Before installing, you can inspect a skill without installing it:

```bash
gh skill preview Jeffallan/claude-skills prompt-engineer
gh skill preview github/awesome-copilot microsoft-agent-framework
```

Read what you are about to install: a skill is instructions that your coding agent will follow.

## What these skills do not override

External skills never take precedence over the project's binding rules: the [architecture contracts](../architecture/current/), the engineering rules in [`agentic/`](../../agentic/) and the router files (`CLAUDE.md`, `AGENTS.md`).

- **Microsoft Agent Framework.** The `microsoft-agent-framework` skill gives generic guidance (for example autonomous agents,
  tool use by agents, a preference for specific cloud services). In bOps the architecture contracts win, in particular
  [C01-R01 (was ADR-0052)](../architecture/current/01-maf-agent-infrastructure.md#c01-r01--maf-is-infrastructure-never-operational-authority) and
  [ADR-0054](../architecture/adr/README.md#adr-0054) (still *Proposed*, decision D1 open) on the MAF boundary. Tool execution,
  policy and agent authority stay with the bOps runtime: the model proposes, the runtime decides and executes. No generic
  execution tool, no tool loop owned by MAF, and the core names no provider.
- **Governed prompts.** Changes to prompts or to the requests the runtime builds must respect
  [C03-R05 (was ADR-0046)](../architecture/current/03-task-state-resume-mutations.md#c03-r05--step-scoped-tool-routing), the PA-1 golden requests
  (`tests/bOps.AgentFramework.Tests/Golden/`) and their tests. A prompt-engineering suggestion is not a reason to regenerate a
  golden file.
- **Authority of the documents.** The skills are support tools, not project specifications. When a skill and a contract disagree, follow
  the contract and, if the contract looks wrong, propose a change to the operator.

## What must not be committed

- Third-party skills under `.claude/skills/` (the two directories above are ignored).
- Personal Claude Code configuration (`.claude/settings.local.json` is already ignored).
- Credentials, benchmark fixtures or any local data.
