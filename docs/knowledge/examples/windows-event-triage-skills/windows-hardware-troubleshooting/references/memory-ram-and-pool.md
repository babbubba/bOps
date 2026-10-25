# Memoria RAM e corruzione di memoria

**Quando si applica:** bugcheck su riferimenti di memoria (0x50 e simili), crash applicativi con eccezioni di memoria in processi diversi, sospetto RAM.

## Evidenze che indicano la RAM

- Bugcheck e crash vari e *non ripetitivi* (codici e moduli diversi) con eccezioni di accesso alla memoria.
- Errori WHEA di memoria (corretti o fatali), risultati di diagnostica memoria con errori (evento MemoryDiagnostics-Results).
- Scomparsa dei sintomi rimuovendo moduli o con impostazioni di memoria piu conservative (verifica dell'operatore).

## Evidenze che NON bastano

- Un solo 0x50 o un solo crash applicativo.
- Il modulo citato in un bugcheck: puo essere la vittima.
- "Memoria disponibile bassa": e pressione di memoria (vedi `windows-cpu-memory-and-reporting`), non guasto.

## Cause alternative

Driver che scrivono memoria altrui, antivirus, file system corrotto, impostazioni di memoria/XMP/overclock, alimentazione.

## Conclusioni giustificate

"I sintomi sono compatibili con corruzione di memoria; la causa (hardware o software) non e determinata."

## Conclusioni non giustificate

"RAM difettosa" senza test di memoria con errori o senza rimozione/sostituzione di prova; scegliere il modulo da sostituire.

## Ipotesi da verificare

Modulo difettoso, impostazioni instabili, driver. Ordina: test di memoria, impostazioni predefinite, driver sospetti (Driver Verifier), modulo per modulo.

## Prossimi controlli

Windows Memory Diagnostics (o strumento equivalente) con esito, stato dei profili di memoria nel firmware, analisi di piu dump, aggiornamenti recenti.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x50--page-fault-in-nonpaged-area
- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x124---whea-uncorrectable-error
