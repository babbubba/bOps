# Bugcheck 0x50 PAGE_FAULT_IN_NONPAGED_AREA

**Quando si applica:** codice 0x50 (80), spesso con un modulo citato (driver, antivirus, file system).

## Che cosa significa (Microsoft)

E stata referenziata memoria di sistema non valida: tipicamente l'indirizzo e sbagliato o punta a memoria gia liberata. Parametri: 1 = indirizzo referenziato; 2 = operazione (su x64 recenti: 0 lettura, 2 scrittura, 10 esecuzione); 3 = indirizzo dell'istruzione che ha referenziato; 4 = tipo di page fault (per esempio PTE liberata, tabella delle pagine non presente, indirizzo non canonico, accesso inconsistente a indirizzo utente).

## Cause documentate

Driver o servizio di sistema difettosi; software antivirus; volume NTFS corrotto; hardware nuovo o guasto, in particolare RAM (principale, cache L2, memoria video).

## Evidenze da cercare

- Modulo indicato (`MODULE_NAME`, `KiBugCheckDriver`) e se si ripete tra i dump.
- Tipo di operazione (P2) e indirizzo (P1): indirizzo basso (NULL) suggerisce un puntatore nullo; indirizzo non canonico un puntatore corrotto.
- Eventi di disco/NTFS nella stessa finestra; test di memoria.
- Modifiche recenti: nuovo driver, nuovo antivirus, nuovo hardware.

## Conclusioni giustificate

"Bugcheck 0x50: riferimento a memoria non valida; modulo X in esecuzione." Ricorrenza dello stesso modulo, se osservata.

## Conclusioni non giustificate

- "RAM difettosa" dal solo codice: e una delle cause possibili, non l'unica.
- Incolpare il modulo citato se compare una sola volta o e un componente di sistema.
- Collegarlo a eventi WHEA senza correlazione dimostrata.

## Ipotesi da verificare

Driver con bug (verificabile con Driver Verifier sul sospetto), antivirus, corruzione del file system, memoria difettosa (test di memoria, rimozione di moduli).

## Prossimi controlli

`!analyze -v` del dump, `lm t n`, Windows Memory Diagnostics (risultato in MemoryDiagnostics-Results), `chkdsk`, aggiornamento o rimozione del driver sospetto, confronto di piu dump.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x50--page-fault-in-nonpaged-area
