# AF-05H2-R1 — Recupero bounded da replan malformato dopo finalizzazione prematura

**Stato: NEXT / READY FOR CODING.** Data 2026-10-12. Base codice: `fix/af-05h-live-regression-hardening` (AF-05H H2+H4, commit `07e422a4c9c6`). Branch nuovo **dal codice AF-05H**, non dal branch dei documenti. Scope esclusivo: replan fallito nella classe **early-final con step pendenti**. Nessun merge/push automatico; niente subagent.

## Evidenza causale osservata

BENCH-00 live su Windows, stesso config snapshot/provider/model:
- AF-05G `97507aa7a914`: B01/B02/B03 tutti Completed, 239.916 token totali, ma B03 era Completed con controllo pianificato mancante.
- AF-05H `07e422a4c9c6`: B01/B02 Completed, B03 Failed, 318.591 token totali.
- B03 AF-05H: `AgentRunner` registra `final answer proposed while plan revision 0 still had unresolved steps`; in `ReplanAsync` due risposte del modello `finish_reason=length`, 4096 output token ciascuna, nessun replan parseabile; `ModelProtocolException(MalformedResponse)` causa `Failed`.
- B01 AF-05H: H2 replan accepted con zero step; questo può essere accettabile se i controlli rimanenti sono facoltativi, ma non si deve perdere copertura richiesta dal goal.
- B02 AF-05H: molteplici EvidenceRead proposte (39 vs 16), +113.646 token vs AF-05G; questo problema è **fuori scope** per R1, trattato in AF-EVID-01.
- Zip originali live sono privati: non copiarli in repo né nel prompt/commit.

## Obiettivo misurabile

In una finalizzazione proposta con piano valido e step non risolti, **non accettare la final answer**. Tenta un replan limitato come oggi, ma se entrambe le risposte sono malformate/troncate, recupera **deterministicamente** il prossimo passo pendente del piano corrente **solo quando sicuro, non condizionale irrisolto e sotto le stesse regole di validazione/autorizzazione**, senza inventare un piano dal testo del modello. Se non esiste una ripresa semanticamente affidabile o si esaurisce un budget, terminare con stato non-successo esplicito, traccia completa e senza loop.

## Specifica d'implementazione

1. **Traccia origine della richiesta di replan**: distingui `EarlyFinalWithUnresolvedPlan` dai normali trigger C8 (policy denial, unknown tool, timeout, semantic mismatch, exhausted plan, verification refuted, ecc.). Non applicare il fallback ai trigger diversi da early-final.
2. **Evita un retry ripetitivo**: su risposta con `finish_reason=length` / output troncato, non ripetere identica richiesta con identico budget quando sicuramente non potrà produrre un piano. Una singola strategia di retry più compatta è consentita solo se specificata, testata e bounded; conservare esattamente tutte le ModelCall, gli outcome e i token consumati.
3. **Fallback limitato**: se il replanning non produce un piano valido, usa il precedente piano ancora persistito, col cursor/position già calcolato, per riprendere il **primo step effettivamente pendente**. Non ricominciare il piano dall'indice zero, non rieseguire step eseguiti, non considerare eseguiti step saltati se non risolti dai contratti condizionali.
4. **Precondizioni di sicurezza**: il piano corrente e la posizione devono essere consistenti; il prossimo passo deve essere eseguibile nel contesto/branch corrente, con tool ancora disponibile; tool policy/entitlement/step authority rimangono applicati normalmente nel percorso di esecuzione; nessuna auto-approval; se rischio di side effect, mutation intent sconosciuto o stato di esecuzione incerto, **non** continuare automaticamente. Nessuna escalation di tool visibility o permessi per far quadrare il fallback.
5. **Anti-loop**: rendi monotona la progressione (cursor/step effettivo) per early-final ripetuti; imposta cap per tentativi di recovery o usa contatori/budget esistenti senza creare tentativi gratuiti. Dopo un numero bounded di early-final/replan-failure senza esecuzione utile, termina `Failed` o altro stato terminale di mancato completamento appropriato; mai `Completed` ingiustificato.
6. **Persistenza/forensics/resume**: la proposta finale prematura deve restare un bookkeeping step distinto; preserva le due chiamate di replan fallite, `finish_reason`, risposta grezza, token/limiti e motivo del recovery; salva progress e cursore in modo compatibile con resume e fencing. No rewrite di audit o output originali. Se i tipi di `TaskState` non permettono un recovery persistito verificabile, documenta e aggiungi il minimo supporto necessario con test di resume.
7. **Budget**: valutare limiti `MaxTotalTokens`, `MaxReplans`, `MaxLifetimeReplans`, step/attempt duration e cancellazione **prima di ogni ripresa**. Non trasformare budget exceeded in successo.
8. **Distinguere root-cause**: una risposta malformata per output limitato non è un HTTP 500. Se fallisce per errori non recuperabili di rete/provider/credenziali, non mascherarli con fallback. Non usare come fallback un replan vuoto generato ad hoc: un replan vuoto valido può essere accettato solo tramite normale validazione del replan.
9. **Contenimento dello scope**: niente H1 context thresholds, H3 correction conflict, H5 prompt changes, EvidenceRead dedup, knowledge/playbook, modifiche a provider/modello, policy, packaging o BENCH-00. Lascia H4 invariato.

## Casi di test obbligatori (offline, deterministici)

- Risposta finale con piano interamente completato: Completed normale, nessun replan.
- Early-final con step pendente read-only e replan valido: segue nuovo piano, comportamento attuale.
- Early-final con step pendente read-only e replan malformed; `finish_reason=length` anche al retry: riprende **solo** step pendente, non lo salta né lo ripete; poi finalizza soltanto quando coverage valida.
- Replan fallito per errori di rete, auth, cancellation, budget, invalid context: gestione precedente/terminazione protetta, **nessun fallback**.
- Pending step condizionale non risolto, tool rimosso o mutazione con outcome incerto: **non** eseguire fallback rischioso.
- Early-final continuo senza progressi: bounded terminale non-successo, no infinite loop e nessun aumento indefinito dei token.
- Resume tra bookkeeping early-final, ModelCall fallite e recovery: stesso risultato e no doppia chiamata tool.
- Per ogni caso: uso deterministico dello step cursor, audit immutabile, token accounting corretto, unauthorized/unknown-outcome guard rispettati.
- Fixtures con model fake che termina con `finish_reason=length`, non dipendere da disponibilità Spark.

## Criteri di accettazione / uscita

- Build e test mirati Windows/Linux (CI dove disponibile); riportare test count, categorie coperte e delta file. Nessun live test eseguito da Codex.
- Nelle fixture sintetiche B03 H2 non fallisce solo perché il replan supera il limite di output quando la continuazione del vecchio piano è sicura, e non diventa `Completed` prima dei passi davvero obbligatori.
- Nessuna modifica alle policy di autorizzazione, ai controlli side effects o alla disposizione delle prove raccolte.
- Un commit isolato sul branch codice nuovo, SHA + istruzioni replay BENCH-00 1×3 per l'operatore. Se il recovery richiede un redesign del runner o rischia di violare autorità del piano, **non implementarlo**: scrivi analisi/gate blocker con proposta di percorso minimale.

## Fasi successive (fuori scope)

- `BENCH-01`: live mirato 1×3 sul nuovo candidato, confronto con due ZIP originali; fermarsi se regressione grave.
- `AF-EVID-01`: contenere ripetizioni EvidenceRead con metriche proposed/served/duplicate, senza confondere costi con correctness.
- `AF-05H4-VERIFY`: ablation H4.
- `BENCH-00` ufficiale 3×3 solo dopo stabilizzazione e policy-hash collector fix.
- Poi decisione PR #100 e ARCH/KNOW roadmap.
