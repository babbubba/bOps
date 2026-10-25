# Storage, file di paging e memoria virtuale

**Quando si applica:** eventi di I/O, dump mancanti, 0x50 con file system, spazio libero, paging.

## Evidenze

- Eventi di disco/volume/controller ripetuti (vedi `windows-event-log-sources`).
- Spazio libero basso sul volume di sistema o del paging; file di paging assente o troppo piccolo; volmgr 46.
- Dati di salute del disco (SMART/stato) se forniti.

## Interpretazione

- Il paging serve il commit di memoria e la scrittura dei dump: una configurazione insufficiente puo impedire il dump e ridurre la diagnosi, senza essere causa del crash.
- Errori di I/O non identificano da soli disco, cavo, controller o driver.
- Un volume corrotto puo causare bugcheck sul file system o 0x50.

## Conclusioni giustificate

"Il volume ha X di spazio libero; il paging e configurato come Y; il dump non e stato prodotto (volmgr 46)."

## Conclusioni non giustificate

"Disco da sostituire" senza errori ripetuti e coerenti o dati di salute; "il paging ha causato il crash".

## Ipotesi da verificare

Salute del dispositivo, firmware, driver del controller, spazio insufficiente per commit e dump.

## Prossimi controlli

Salute del disco, configurazione del paging e del dump, `chkdsk` in sola diagnosi, spazio libero nel tempo.

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart
