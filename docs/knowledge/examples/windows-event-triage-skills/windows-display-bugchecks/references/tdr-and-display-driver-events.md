# TDR ed eventi del driver video

**Quando si applica:** eventi della sorgente Display ("Display driver ... stopped responding and has recovered"), schermo nero momentaneo, flicker, 0x116/0x117/0x141.

## Come funziona il TDR

Lo scheduler GPU (dxgkrnl) rileva che un lavoro non termina entro il timeout (di default 2 secondi), prova a prelazionarlo e, se non ci riesce, resetta il driver e la GPU. Il risultato visibile e un flicker e il messaggio "Display driver stopped responding and has recovered", registrato anche nell'Event Viewer. Il recupero **riuscito** non riavvia il sistema.

Codici collegati: 0x117 (timeout rilevato, il sistema non e stato ripristinato), 0x141 (timeout di un engine), 0x142 (applicazione bloccata). Di default il sistema esegue il bugcheck quando rileva cinque o piu hang GPU in un minuto.

## Evidenze da cercare

- Eventi Display (di solito ID 4101) con il nome del driver: confermalo nel testo.
- Sequenze ravvicinate: molti TDR in un minuto aumentano il rischio di 0x116/0x117.
- Live dump 193/141 e schermo nero associati.
- Processi grafici che crashano insieme (finestre che si chiudono, app che perdono il device).

## Conclusioni giustificate

"N recuperi TDR riusciti, il sistema non si e riavviato in quei casi." "Il driver nominato nei messaggi e X."

## Conclusioni non giustificate

- "Crash di sistema" per un TDR riuscito.
- "GPU guasta" dalla sola presenza di TDR: driver, carico, temperatura, alimentazione, overclock e applicazioni sono tutte possibilita.

## Ipotesi da verificare

Driver non aggiornato o instabile, carico GPU eccessivo, condizioni termiche o di alimentazione, applicazioni specifiche che scatenano il timeout.

## Prossimi controlli

Correlazione dei TDR con applicazioni in uso, versioni del driver, temperature e potenza della GPU durante il carico, valori di TDR nel registro (solo lettura).

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/display/timeout-detection-and-recovery
- https://learn.microsoft.com/windows-hardware/drivers/display/tdr-changes-in-windows-8
