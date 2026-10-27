# Bugcheck 0x124 WHEA_UNCORRECTABLE_ERROR

**Quando si applica:** codice 0x124 (292) in 1001/Kernel-Power 41/dump, di solito con eventi WHEA-Logger.

## Che cosa significa (Microsoft)

Un errore hardware **fatale** e stato riportato tramite WHEA (Windows Hardware Error Architecture). Il kernel crea un record dell'errore e genera il bugcheck. Parametro 1 = tipo di sorgente dell'errore; parametro 2 = indirizzo del record `WHEA_ERROR_RECORD`. Per P1 = 0 (machine check exception) i parametri 3 e 4 sono la parte alta e bassa di `MCi_STATUS`.

Valori di P1 documentati (selezione): 0 machine check; 1 machine check corretto; 2 errore di piattaforma corretto; 3 NMI; 4 errore PCI Express non correggibile; 5 errore hardware generico; 0x10 errore di driver di dispositivo.

## Evidenze da cercare

- Evento WHEA-Logger **fatale** (livello Error, testo "A fatal hardware error has occurred") poco prima del riavvio, con componente e sorgente.
- Parametro 1 del bugcheck e record WHEA decodificabile (`!errrec`, `!whea`).
- Ripetizione dello stesso componente nei record.
- Contesto: overclock, raffreddamento, memoria, aggiornamenti di firmware.

## Interpretazione tecnica

Microsoft: tipicamente legato a guasti fisici (calore, hardware difettoso, memoria, processore che sta cedendo), meno probabilmente a un driver. E il caso "tipico", non una regola: P1 = 0x10 indica un errore *segnalato da un driver di dispositivo* tramite WHEA.

## Conclusioni giustificate

"Bugcheck 0x124: errore hardware fatale riportato da WHEA, tipo di sorgente P1." Il componente nominato dal record WHEA, se il record e disponibile.

## Conclusioni non giustificate

- Confondere un 0x124 (fatale, riavvio) con errori WHEA **corretti** (ID 17/19: warning, nessun riavvio).
- Nominare il componente da sostituire senza il record WHEA o senza ripetizione.
- Dire che un errore corretto "ha causato" un riavvio: servono un 0x124 o un evento fatale.

## Ipotesi da verificare

CPU/cache, memoria, bus PCIe/dispositivo, alimentazione, calore, impostazioni di overclock. Ogni ipotesi richiede dati indipendenti (test memoria, temperature, log dispositivo).

## Prossimi controlli

Decodifica del record WHEA, test della memoria, rimozione dell'overclock, controllo del raffreddamento e dei ventilatori, firmware, confronto con eventi WHEA corretti nei giorni precedenti.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x124---whea-uncorrectable-error
- https://learn.microsoft.com/windows-hardware/drivers/whea/hardware-errors-and-error-sources
- https://learn.microsoft.com/windows-hardware/drivers/whea/error-processing
