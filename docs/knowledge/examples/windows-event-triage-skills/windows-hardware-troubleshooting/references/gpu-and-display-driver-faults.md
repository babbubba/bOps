# GPU e driver di visualizzazione

**Quando si applica:** TDR, live dump grafici (193, 141, 117), 0x116, schermo nero, artefatti, crash di applicazioni grafiche.

## Evidenze che indicano il sottosistema grafico

- Eventi Display con recupero del driver, TDR ripetuti, live dump dxgkrnl, 0x116/0x117.
- Sintomi legati a carichi grafici; applicazioni grafiche che perdono il dispositivo.
- Modulo del driver video nei dump.

## Cosa indicano e cosa no

- Indicano che **il sottosistema grafico (driver + GPU + software)** ha avuto un problema.
- Non distinguono tra driver, GPU, alimentazione, calore, impostazioni di overclock o applicazione.
- Un live dump (193) non e un crash di sistema. Un TDR riuscito non e un riavvio. Solo 0x116/0x117 sono bugcheck.

## Cause alternative (Microsoft per 0x116)

Driver non aggiornato, overclock, memoria/timing non corretti, raffreddamento insufficiente, alimentazione insufficiente, parti difettose, troppi programmi o effetti visivi.

## Conclusioni giustificate

"Il sottosistema grafico ha prodotto N episodi (TDR/live dump) e M bugcheck in finestre X." Tipi tenuti separati.

## Conclusioni non giustificate

"Scheda video guasta", "driver difettoso" come fatti; contare i live dump come schermate blu.

## Ipotesi da verificare

Instabilita del driver per versione, carico, temperatura/potenza, alimentazione: serve confronto tra versioni di driver, monitoraggio durante il carico, rimozione di overclock.

## Prossimi controlli

Cronologia per versione del driver, correlazione con applicazioni, temperature e consumi sotto carico, analisi del dump (`!analyze`), test in modalita provvisoria.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x116---video-tdr-failure
- https://learn.microsoft.com/windows-hardware/drivers/display/timeout-detection-and-recovery
