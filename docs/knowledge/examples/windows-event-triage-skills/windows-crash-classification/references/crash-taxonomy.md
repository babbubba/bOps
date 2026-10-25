# Tassonomia dei crash Windows

**Quando si applica:** ogni volta che un'evidenza parla di crash, blocco, riavvio, dump o "errore hardware". Classifica prima, spiega dopo.

## I sei tipi di evento (sono diversi)

1. **Bugcheck (BSOD, stop code)**: il kernel si ferma di proposito perche non puo continuare in sicurezza. Il sistema si riavvia (o resta sulla schermata blu). Lascia di norma un dump di memoria e un evento 1001 (WER-SystemErrorReporting, "The computer has rebooted from a bugcheck") e, al riavvio, Kernel-Power 41 con `BugcheckCode` diverso da zero.
2. **Kernel live dump**: il kernel raccoglie un dump **mentre il sistema continua a funzionare**. Non e un bugcheck, non riavvia, non e una schermata blu. Esempi: 0x193 (dxgkrnl), 0x1B0, 0x1A8; in Reliability Monitor appaiono come LiveKernelEvent / "Hardware error".
3. **Crash applicativo**: un processo termina (Application Error 1000, WER 1001 con evento APPCRASH, .NET Runtime 1026). Il sistema operativo resta in vita.
4. **Arresto inatteso / riavvio senza arresto pulito**: Kernel-Power 41 ed EventLog 6008. E un *sintomo* con molte cause (bugcheck, blocco totale, perdita di alimentazione, reset forzato).
5. **Errore del sottosistema display**: reset TDR riuscito (il driver si e ripreso) oppure fallito (bugcheck 0x116/0x117) oppure live dump di dxgkrnl.
6. **Errore hardware segnalato (WHEA)**: errore corretto (non fatale, il sistema continua) oppure fatale (bugcheck 0x124).

## Evidenze da cercare

- Esiste un evento di bugcheck (1001 con codice) nella stessa finestra? Se no, non dire "BSOD".
- Il record e un LiveKernelEvent (dump in `LiveKernelReports`) o un BlueScreen (dump in `Minidump`/`MEMORY.DMP`)?
- Il sistema si e riavviato? Cerca 6005/6009 (avvio), Kernel-Power 41, 6008, assenza di 1074/6006.
- Il processo e terminato da solo (1000) o c'e stato un riavvio?

## Conclusioni giustificate

- "Evento di tipo X con codice Y alle ore Z", se codice, fonte e tempo sono nel dato.
- "Un riavvio inatteso e avvenuto", se Kernel-Power 41/6008 lo registrano.

## Conclusioni non giustificate

- Chiamare BSOD o "riavvio" un live dump.
- Dedurre la causa (GPU, RAM, alimentatore) dalla sola classe di evento.
- Sommare tipi diversi in un unico "numero di crash" senza dichiararlo.

## Ipotesi da verificare

Il tipo identifica *dove* indagare, non *perche*: un 0x116 punta al grafico, ma la causa puo essere driver, alimentazione, calore o hardware.

## Prossimi controlli

Dettagli originali dei record, dump disponibili, cronologia di 41/6008/1001 nella stessa notte, Reliability Monitor, dopo aver stabilito il tipo.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-checks--blue-screens-
- https://learn.microsoft.com/troubleshoot/windows-server/performance/troubleshoot-unexpected-reboots-system-event-logs
