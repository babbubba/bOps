# Eventi di storage, volume e driver

**Quando si applica:** sospetto di disco/SSD, file system o driver coinvolti in blocchi, bugcheck o dump mancanti.

## Eventi tipici (verifica sempre nel messaggio)

- Provider `disk`: errori di I/O (per esempio ID 7 settore danneggiato, 11 errore del controller, 51 errore di paging, 153 I/O ritentato).
- Provider `Ntfs`: problemi di volume o file system.
- `storahci` / `stornvme`: reset del dispositivo o timeout del controller.
- `volmgr` 46: inizializzazione del crash dump non riuscita.
- Service Control Manager: servizi terminati o installazioni di driver/servizi (utile per datare un cambiamento).

Gli ID qui elencati sono indicativi: la fonte di verita e il testo dell'evento. Se il messaggio non e restituito, non applicare il significato a memoria.

## Interpretazione

- Errori di I/O possono derivare da disco, cavo/porta, controller, driver o alimentazione. Non indicano da soli quale.
- Un bugcheck puo essere scatenato da un driver di storage (esempio: errore segnalato via WHEA da un driver di dispositivo, P1 = 0x10 nel 0x124).
- La mancanza di dump puo dipendere da un errore di storage o dalla configurazione del paging.

## Conclusioni giustificate

"N eventi di I/O del provider X sul dispositivo Y tra A e B."

## Conclusioni non giustificate

- "Disco in fase di guasto" senza ripetizione, messaggi coerenti e dati SMART/di salute.
- Collegare errori di storage a riavvii senza correlazione di tempo e tipo.

## Ipotesi da verificare

Salute del disco, firmware, cablaggio, driver del controller, alimentazione, pressione di memoria virtuale.

## Prossimi controlli

Dati di salute del disco (SMART/stato), spazio libero e paging, `chkdsk` in sola diagnosi, aggiornamenti di firmware e driver.

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart
- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x50--page-fault-in-nonpaged-area
