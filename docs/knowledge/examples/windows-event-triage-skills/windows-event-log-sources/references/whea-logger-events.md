# Microsoft-Windows-WHEA-Logger

**Quando si applica:** eventi della sorgente Microsoft-Windows-WHEA-Logger, "errore hardware", MCE, PCIe AER, cache/memoria.

## Che cosa sono

WHEA raccoglie gli errori hardware e li registra come eventi ETW nel registro System. Gli errori sono **corretti** (corretti da hardware/firmware prima che il sistema li veda, non fatali) oppure **non corretti**, a loro volta *fatali* (bugcheck) o *non fatali* (il sistema prova a recuperare; se non riesce, bugcheck).

## Come leggere gli ID (non intercambiabili)

Gli ID differiscono per gravita, tipo di sorgente e componente. Classifica sempre dal **Level**, dal **testo del messaggio** ("corrected" o "fatal") e dal componente/sorgente, non dal solo numero.

- ID 17 (Warning): errore **corretto**, per esempio "Component: PCI Express Endpoint, Error Source: Advanced Error Reporting (PCI Express)".
- ID 19 (Warning): errore **corretto** di processore ("Corrected Machine Check"), con campi come ApicId e MCABank.
- ID 18 (Error): errore **fatale** ("A fatal hardware error has occurred", per esempio Processor Core / Machine Check Exception).
- ID 1 (Error): errore hardware con record grezzo (CPER); la sorgente puo essere varia (anche un driver di dispositivo): leggi il record.
- Altri ID esistono (per esempio 2): non assumere che condividano gravita o significato di 17/19; leggi Level e messaggio. Se il tool riporta solo l'ID, dichiara che la classe e incerta.

I campi numerici (ErrorSource, ErrorType, MCABank, MciStat) richiedono la documentazione WHEA; non decodificarli a memoria.

## Evidenze da cercare

- Corretto o fatale (Level e testo). Componente e sorgente. Ripetizione dello stesso componente, indice di processore o bus.
- Un errore fatale vicino a un riavvio (41/6008) o a un bugcheck 0x124.
- Raffiche: molti errori corretti in pochi secondi (vedi `windows-incident-correlation`).

## Conclusioni giustificate

"N errori WHEA corretti del tipo X (nessun riavvio implicato)." "Un errore fatale alle ore Y seguito da riavvio."

## Conclusioni non giustificate

- "Hardware guasto" o "componente da sostituire" da errori corretti isolati.
- "Gli errori WHEA hanno causato i riavvii" senza un evento fatale o un 0x124 compatibile.
- Sommare corretti e fatali, o ID diversi, in un unico conteggio senza dichiararlo.

## Ipotesi da verificare

Un aumento di errori corretti puo precedere un guasto (memoria, PCIe, processore) o dipendere da alimentazione, temperatura, overclock, firmware. E una tendenza da osservare, non una diagnosi.

## Prossimi controlli

Record completo degli eventi, ripetizione per componente nel tempo, aggiornamenti di firmware, test della memoria, temperature, rimozione dell'overclock.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/whea/hardware-errors-and-error-sources
- https://learn.microsoft.com/windows-hardware/drivers/whea/whea-hardware-error-events
- https://learn.microsoft.com/windows-hardware/drivers/whea/error-processing
