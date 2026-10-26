# PA-1 request-assembly baselines

Expected content for `PromptAssembly/RequestAssemblyGoldenTests`. Each scenario has two files that are produced together:

| File | Role |
|---|---|
| `<scenario>.json` | The assembled provider request bodies of the scenario, exactly as `OpenAiCompatibleChatModel` serialized them (`model`, `messages`, `tools`, `tool_choice`, `max_tokens`), plus the persisted steps of the run. Compared **structurally**: object property order is irrelevant, array order (messages, tools) is not. |
| `<scenario>.md` | The same document rendered for prompt review (system prompt with real line breaks, tools with their schemas, messages by role). Compared **byte for byte** against a rendering of the actual run, so it can never go stale. |

They were captured at commit `7053ca0`, defects included (PRA-R2, PRA-R3, PRA-R4, PRA-P1), as the measuring baseline of the
prompt-routing finalization. **PROMPT-ROUTING-FINALIZATION (2026-10-19) re-captured them on purpose**: PA1-02B, 03, 04, 05, 06, 07, 08A, 08B, 09
and 10 changed (plan-end paragraph, one-tool explanation, step routing tail, early-final replan lead, RC1/RC2 text); PA1-01 and PA1-02A are
byte-identical to the baseline. The intended differences are listed in `docs/benchmarks/prompt-routing-finalization.md`. They are not a
statement of what a prompt should say. A difference means "what the
model is sent changed" and needs a decision: change these files in the same commit as the prompt, never to silence a failure.
**BOPS-0125 (2026-10-26, C03-R09 amendment) re-captured PA1-01, PA1-02A, PA1-02B and PA1-09 on purpose:** the planning and replanning
system prompts gained one paragraph stating the attempt's step budget (`This execution attempt has N steps left. …`, from
`PlanStepBudget`), between the instructions and the tool catalog. Removing that paragraph from the new capture gives the previous bytes;
every other scenario is unchanged.

| Scenario | Frozen requests |
|---|---|
| PA1-01 | planning |
| PA1-02A / PA1-02B | ordinary replan / early-final replan |
| PA1-03 | step with a pending operational tool |
| PA1-04 | step with Evidence Read (index, then continuation after the read) |
| PA1-05 | step with Knowledge (index and control function, then continuation after the read) |
| PA1-06 | plan exhausted |
| PA1-07 | recovery RC1 (empty reply) |
| PA1-08A / PA1-08B | recovery RC2 (output limit) with a tool pending / with the plan exhausted |
| PA1-09 | LIVE-05 continuation after a failed early-final replan |
| PA1-10 | evidence grounding check |

The scenarios are scripted and offline: a fixed task id, synthetic goal, synthetic read-only tools, a scripted HTTP upstream. Nothing
depends on time, a credential, a network, the operating system, or a random value; two captures in two processes were byte-identical.

To re-capture (only after a decision that the prompts change):

```
set BOPS_PA1_CAPTURE_DIR=<output directory>
dotnet test tests/bOps.AgentFramework.Tests --filter FullyQualifiedName~PromptAssembly.RequestAssemblyGoldenTests
```

Capture mode writes the files and compares nothing; copy them over this directory and review the diff of the `.md` files.

What this baseline does **not** show: the text llama.cpp finally tokenizes. The server applies its own chat template to `messages`
and `tools`; that step is outside the request body and is not verified here.
