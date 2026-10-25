# LiveKernelEvent: leggere i codici di Reliability Monitor e WER

**Quando si applica:** Reliability Monitor mostra "Hardware error", oppure WER 1001 riporta `Event Name: LiveKernelEvent`.

## Che cosa e

Un LiveKernelEvent e un rapporto WER associato a un **live kernel dump** (o a un report di watchdog) raccolto mentre il sistema funzionava. La descrizione utente e generica ("A problem with your hardware caused Windows to stop working correctly") ma il sistema **non e necessariamente andato in crash**. I file stanno in `C:\Windows\LiveKernelReports\<tipo>` (per esempio WATCHDOG, NDIS, AMD_WATCHDOG).

## Codici osservati in pratica (esempi, non lista esaustiva)

- 193: dxgkrnl live dump (VIDEO_DXGKRNL_LIVEDUMP).
- 141: timeout di un engine grafico (watchdog).
- 117: timeout GPU rilevato (adapter).
- 1b8, 15e, 144, a1000005 e altri: codici specifici di componente o vendor; il significato richiede la documentazione del componente e il dump.

Un codice sconosciuto non va "tradotto" a memoria: dichiaralo come codice non interpretato.

## Evidenze da cercare

- Codice e parametri P1..P4, data/ora del rapporto, file `.dmp` associato.
- Presenza di BlueScreen veri nello stesso periodo (con codice e Kernel-Power 41).
- Sintomi e processi che crashano contemporaneamente (spesso vittime del reset del driver).

## Conclusioni giustificate

"Sono stati registrati N LiveKernelEvent con codice X." "Non risultano bugcheck associati nelle evidenze."

## Conclusioni non giustificate

- Chiamarli crash di sistema o schermate blu.
- Attribuire il componente dal solo nome della cartella o del vendor.
- Assegnare un significato inventato a un codice non documentato.

## Ipotesi da verificare

Il componente che ha generato il watchdog e instabile o sotto stress; verifica con analisi del dump e correlazione con carichi di lavoro.

## Prossimi controlli

Raggruppare per codice e giorno, leggere i `.dmp`/`WERInternalMetadata.xml` se forniti, confrontare con TDR e con aggiornamenti dei driver.

## Riferimenti

- https://learn.microsoft.com/windows/win32/wer/windows-error-reporting
- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x193--video-dxgkrnl-livedump
