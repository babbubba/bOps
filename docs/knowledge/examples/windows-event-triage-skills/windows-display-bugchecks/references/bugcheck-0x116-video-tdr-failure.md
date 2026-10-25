# Bugcheck 0x116 VIDEO_TDR_FAILURE

**Quando si applica:** codice 0x116 (278) in 1001/Kernel-Power 41/dump; spesso un modulo del driver video (per esempio nvlddmkm, amdkmdag, igdkmd).

## Che cosa significa (Microsoft)

Il tentativo di resettare il driver di visualizzazione e recuperare da un timeout **e fallito**. Il TDR (Timeout Detection and Recovery) rileva che la GPU non completa il lavoro entro il timeout (predefinito 2 secondi) e riporta il driver in stato noto. Se i thread non terminano in tempo durante il reset, o se in poco tempo avvengono troppi TDR (di default piu di cinque in un minuto), il sistema esegue il bugcheck 0x116. Parametri: 1 = contesto di recupero TDR; 2 = puntatore al modulo responsabile; 3 = ultimo codice NTSTATUS; 4 = dati interni.

## Differenza dagli altri eventi grafici

- TDR **riuscito**: "Display driver stopped responding and has recovered": nessun riavvio.
- Live dump 0x193: nessun riavvio.
- **0x116**: bugcheck vero, il sistema si riavvia.

## Evidenze da cercare

- Evento 1001 / Kernel-Power 41 con `BugcheckCode` 0x116, e eventi TDR nei minuti precedenti.
- Modulo del driver in P2 / `!analyze`.
- Frequenza dei TDR riusciti prima del bugcheck.

## Conclusioni giustificate

"0x116: il recupero TDR e fallito; il modulo responsabile indicato e X."

## Conclusioni non giustificate

- "La scheda video e guasta": Microsoft elenca driver non aggiornati, overclock, raffreddamento, alimentazione insufficiente, parti difettose, carico elevato.
- Scambiare TDR riusciti o live dump per 0x116.

## Ipotesi da verificare

Driver, overclock/impostazioni di memoria, raffreddamento, alimentazione insufficiente, componenti difettosi, carico di lavoro grafico.

## Prossimi controlli

Aggiornamento/pulizia del driver, rimozione di overclock, test in modalita provvisoria, verifica temperature e alimentazione, test di memoria.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x116---video-tdr-failure
- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x117---video-tdr-timeout-detected
- https://learn.microsoft.com/windows-hardware/drivers/display/timeout-detection-and-recovery
