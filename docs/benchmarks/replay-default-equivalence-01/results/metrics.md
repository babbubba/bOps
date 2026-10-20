| run | exec status | s | LLM calls | by phase | prompt tok | compl tok | length | failed | replans | prem. finals | tools executed | redundant reads | unsupported replay |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| T02-r1 | Completed | 110.7 | 4 | {'planning': 1, 'step': 2, 'step-after-tool-result': 1} | 31862 | 5777 | 0 | 0 | 0 | 0 | ['system.events'] | 0+0 | 0 |
| T02-r2 | Completed | 284.5 | 9 | {'planning': 1, 'step': 3, 'step-after-tool-result': 3, 'step-after-runtime-reply(EvidenceRead/v1 Success; offset=0; retur)': 1, 'step-after-runtime-reply(EvidenceRead/v1 Success; offset=4000; re)': 1} | 60015 | 16404 | 1 | 0 | 0 | 0 | ['system.events', 'system.stability', 'system.crashes'] | 0+1 | 2 |
| T02-r3 | Completed | 312.8 | 9 | {'planning': 2, 'replanning': 1, 'step': 2, 'step-after-tool-result': 4} | 92132 | 16616 | 0 | 0 | 1 | 1 | ['system.events', 'system.events', 'system.stability'] | 0+0 | 2 |
