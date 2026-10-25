# Kernel live dump contro bugcheck

**Quando si applica:** compaiono LiveKernelEvent, `LiveKernelReports`, il codice 0x193 o altri codici "...LIVEDUMP".

## Differenza essenziale

**Un kernel live dump non e un BSOD e non riavvia il sistema.** Contare i live dump come schermate blu o come riavvii e un errore di classificazione.

- **Bugcheck**: il sistema si ferma e di norma si riavvia. Lascia 1001 (bugcheck) e, al riavvio, Kernel-Power 41 con `BugcheckCode` non zero.
- **Live dump**: il kernel scatta un'istantanea mentre il sistema *continua a girare*. Microsoft documenta che questi codici (per esempio 0x193 VIDEO_DXGKRNL_LIVEDUMP) "non possono mai essere usati per un vero bugcheck: servono a identificare i live dump". Il riavvio non e un effetto di questo evento.

## Evidenze da cercare

- Reliability Monitor / WER 1001 con `Event Name: LiveKernelEvent` e un codice (per esempio 193, 141, 117, 1b8, a1000005).
- File in `C:\Windows\LiveKernelReports\<componente>`.
- Per un vero BSOD invece: `Event Name: BlueScreen` con P1 = codice bugcheck, `Minidump`/`MEMORY.DMP`, 1001 in System, Kernel-Power 41.
- Controlla se Kernel-Power 41 / 6008 cadono vicino al live dump: solo allora c'e un riavvio, e va provato con quegli eventi, non col live dump.

## Conclusioni giustificate

- "Sono stati registrati N live dump di tipo X: eventi non fatali per il sistema."
- "Nessun bugcheck associato nelle evidenze raccolte" (se manca 1001 e BugcheckCode e zero).

## Conclusioni non giustificate

- "Il PC ha avuto N schermate blu" contando live dump.
- "Il live dump ha causato il riavvio" senza un 41/6008 correlato e dettagli compatibili.
- "La GPU e guasta" solo perche il live dump e di dxgkrnl: segnala un problema rilevato dal sottosistema grafico, non il componente difettoso.

## Ipotesi da verificare

Un live dump ripetuto puo indicare un driver o un componente che sta fallendo ma si riprende. E un'ipotesi: servono il dump analizzato, il driver coinvolto e la correlazione con sintomi (schermo nero, blocchi).

## Prossimi controlli

Cronologia dei live dump per codice e giorno; presenza di sintomi utente; analisi del dump (`!analyze`) se disponibile; versione del driver video e date di aggiornamento.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x193--video-dxgkrnl-livedump
- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x1b0--video-miniport-failed-livedump
