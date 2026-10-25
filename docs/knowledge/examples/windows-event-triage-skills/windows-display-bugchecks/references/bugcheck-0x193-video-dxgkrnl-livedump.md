# 0x193 VIDEO_DXGKRNL_LIVEDUMP: un live dump, non un BSOD

**Quando si applica:** LiveKernelEvent con codice 193 (hex) in Reliability Monitor / WER 1001, oppure dump con origine dxgkrnl in `LiveKernelReports`.

## Che cosa significa (Microsoft)

0x193 indica che dxgkrnl (il kernel grafico, DirectX Graphics Kernel) ha generato un **live dump**. Microsoft precisa: "questo codice non puo mai essere usato per un vero bug check; serve a identificare i live dump". Parametro 1 = codice del motivo (documentato: 0x100 interno); parametri 2-4 riservati. In WER appare come LiveKernelEvent, riepilogo "Hardware error".

## Differenza dal bugcheck

Il sistema **non si ferma e non si riavvia** per un 0x193. Nessun Kernel-Power 41 e implicato da esso; il live dump esiste proprio perche il computer continua a funzionare. Il parametro 1 puo avere altri valori nei report reali (per esempio 0x80e): non dedurne un significato senza documentazione.

## Evidenze da cercare

- Numero e distribuzione temporale dei live dump 193 (per giorno).
- Sintomi utente concomitanti: schermo nero, blocco momentaneo, ripristino del driver.
- Eventi display (TDR, "Display driver ... stopped responding and has recovered") vicini.
- Se esiste *anche* un bugcheck 1001/Kernel-Power 41 vicino: e un evento distinto, da provare separatamente.
- Driver video e data dell'ultimo aggiornamento.

## Conclusioni giustificate

"N live dump di dxgkrnl (0x193) sono stati registrati: il sottosistema grafico ha rilevato una condizione anomala e il sistema e rimasto in funzione."

## Conclusioni non giustificate

- "Schermate blu" / "BSOD" / "riavvii" riferiti ai 193.
- "La GPU e guasta" o "il driver e difettoso" come fatti: sono ipotesi.
- Sommare i 193 ai bugcheck veri in un unico conteggio di crash.

## Ipotesi da verificare

Problema del driver video o della sua interazione con il carico (giochi, accelerazione hardware), temperature/alimentazione della GPU, firmware. Verifica con analisi del dump, versioni del driver, esito dei TDR.

## Prossimi controlli

Cronologia dei 193 prima/dopo aggiornamenti del driver, correlazione con TDR e con schermo nero, analisi del dump, test con driver alternativo (decisione dell'operatore).

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x193--video-dxgkrnl-livedump
- https://learn.microsoft.com/windows-hardware/drivers/display/timeout-detection-and-recovery
