| run | exec status | s | LLM calls | by phase | prompt tok | compl tok | length | failed | replans | prem. finals | tools executed | redundant reads | unsupported replay |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| T02-r1 | Completed | 169.7 | 5 | {'planning': 1, 'step': 2, 'step-after-tool-result': 2} | 17979 | 10076 | 1 | 0 | 0 | 0 | ['system.events', 'system.events'] | 0+0 | 1 |
| T02-r2 | Failed | 256 | 10 | {'planning': 1, 'step': 2, 'step-after-tool-result': 4, 'step-after-runtime-reply(EvidenceRead/v1 Success; offset=0; retur)': 3} | 35942 | 15316 | 1 | 0 | 0 | 0 | ['system.events', 'system.stability', 'system.stability', 'system.crashes'] | 0+2 | 3 |
| T02-r3 | Completed | 68.4 | 9 | {'planning': 1, 'step': 1, 'step-after-knowledge-read': 5, 'step-after-runtime-reply(EvidenceRead/v1 Success; offset=0; retur)': 2} | 38591 | 3686 | 0 | 0 | 0 | 0 | ['system.events', 'system.events', 'system.stability'] | 0+1 | 2 |
| T08-r1 | Completed | 44 | 3 | {'planning': 1, 'step': 1, 'step-after-knowledge-read': 1} | 9482 | 2507 | 0 | 0 | 0 | 0 | [] | 0+0 | 0 |
| T08-r2 | Completed | 302.6 | 6 | {'planning': 2, 'step': 1, 'step-after-tool-result': 1, 'step-after-knowledge-read': 1, 'step-after-deferred-call-notice': 1} | 22897 | 18229 | 1 | 0 | 0 | 0 | ['system.info'] | 0+0 | 0 |
| T08-r3 | Failed | 430 | 6 | {'planning': 2, 'step': 2, 'step-after-tool-result': 2} | 21438 | 25992 | 3 | 0 | 0 | 0 | ['system.devices', 'system.info'] | 0+0 | 1 |
| T10-r1 | ReplanLimitReached | 353.2 | 12 | {'planning': 1, 'replanning': 2, 'step': 1, 'step-after-knowledge-read': 6, 'rejected-final-continuation': 1} | 54877 | 20595 | 1 | 0 | 2 | 3 | ['system.info', 'system.events', 'system.events'] | 0+0 | 0 |
| T10-r2 | Completed | 288.3 | 11 | {'planning': 1, 'replanning': 3, 'step': 1, 'step-after-knowledge-read': 6} | 47862 | 16987 | 0 | 0 | 3 | 3 | ['web.fetch', 'web.fetch'] | 0+0 | 0 |
| T10-r3 | Failed | 319.1 | 10 | {'planning': 1, 'replanning': 2, 'step': 1, 'step-after-knowledge-read': 5} | 49723 | 18804 | 1 | 0 | 2 | 3 | ['system.events'] | 0+0 | 1 |
