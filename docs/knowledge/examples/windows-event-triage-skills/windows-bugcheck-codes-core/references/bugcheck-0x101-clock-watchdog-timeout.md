# Bugcheck 0x101 CLOCK_WATCHDOG_TIMEOUT

**Quando si applica:** codice 0x101 in 1001, Kernel-Power 41 (`BugcheckCode` 0x101/257) o in un dump.

## Che cosa significa (Microsoft)

Un interrupt di clock atteso su un processore *secondario*, in un sistema multiprocessore, non e arrivato entro l'intervallo previsto. Parametri: 1 = intervallo di timeout in tick di clock nominali; 2 = 0; 3 = indirizzo del PRCB del processore che non risponde; 4 = indice del processore bloccato. Causa documentata: il processore non elabora interrupt, in genere perche non risponde o e in deadlock.

## Evidenze da cercare

- Codice 0x101 con parametri 3 e 4 (quale processore).
- Ripetizione dello stesso indice di processore oppure processori diversi.
- Dump: stato dei processori, IRQL, codice in esecuzione (`!pcr`, `!prcb`, `!irql`, stack).
- Contesto: overclock, undervolt, firmware/BIOS recente, virtualizzazione, eventi WHEA correlati nella stessa finestra.

## Interpretazione tecnica

E un sintomo di "un core non avanza": puo derivare da codice kernel che mantiene il processore a IRQL alto (driver), da firmware/BIOS, da instabilita di CPU/alimentazione o da bug dell'hypervisor. Il codice non distingue tra queste.

## Conclusioni giustificate

"Il sistema ha registrato 0x101: un processore secondario non ha risposto agli interrupt." Il processore indicato dal parametro 4, se riportato.

## Conclusioni non giustificate

- "CPU difettosa" senza errori WHEA/MCE o ripetizione compatibile.
- "Alimentatore guasto" o "temperature alte" senza dati indipendenti.
- Equiparare 0x101 a 0x124 o a un blocco senza codice.

## Ipotesi da verificare

Driver che blocca un core; impostazioni di overclock o firmware; instabilita elettrica o termica; problema dell'hypervisor. Ordina le verifiche dalle piu economiche e reversibili.

## Prossimi controlli

Valori di default del firmware, aggiornamento BIOS/chipset, assenza di overclock, WHEA-Logger nella stessa finestra, analisi del dump, test di stress controllato, confronto con altri codici bugcheck.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x101---clock-watchdog-timeout
