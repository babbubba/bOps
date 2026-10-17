# Request baselines of d3da032

Expected bytes for `KnowledgeGoldenRequestTests` (KNOW-DISC-01 T8-FIX01, ADR-0057 H.1 row 9). They are the model requests the runner
built at commit `d3da032` (the last commit before the knowledge index), for one fixed scripted task. They are never regenerated from
the current tree: a difference is a change of what the model is sent and needs a decision.

| File | Content |
|---|---|
| `legacy-nohost.json` | all five requests of the scenario, runner without a knowledge host |
| `legacy-notbound.json` | the same, host present, no pack bound to the tenant (`NotBound`) |
| `planning-replan-bound.json` | the planning request and the replan request, host present and a knowledge pack bound |

To reproduce them:

```
git archive d3da032 | tar -x -C <empty short directory>
copy tests/bOps.AgentFramework.Tests/KnowledgeGoldenRequestTests.cs into the same place of that copy
set BOPS_GOLDEN_CAPTURE_DIR=<output directory>
dotnet test tests/bOps.AgentFramework.Tests --filter FullyQualifiedName~KnowledgeGoldenRequestTests
```

Capture mode writes the files and compares nothing. The files are one JSON document per request, separated by `\n`, UTF-8 without BOM.
Platform names of the offered tools are masked (`*`); nothing else is normalized.
