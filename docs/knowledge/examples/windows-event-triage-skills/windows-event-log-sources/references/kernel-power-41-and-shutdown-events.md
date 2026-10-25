# Kernel-Power 41, EventLog 6008 e gli eventi di arresto

**Quando si applica:** riavvii inattesi, "il PC si riavvia da solo", cronologia degli arresti.

## Eventi e significato

| Evento | Provider | Significato |
|---|---|---|
| 41 | Microsoft-Windows-Kernel-Power (Critical) | Il sistema si e riavviato senza arresto pulito: potrebbe essersi bloccato, essere andato in crash o aver perso alimentazione. Registrato **al riavvio successivo**. |
| 6008 | EventLog | "L'arresto precedente alle <ora> del <data> e stato inatteso." Stessa vicenda di 41, un'altra registrazione. |
| 6006 | EventLog | Servizio EventLog fermato in modo regolare (arresto pulito). |
| 6005 | EventLog | Servizio EventLog avviato (avvio del sistema). |
| 1074 | User32 | Arresto o riavvio avviato da un'applicazione o dall'utente (voluto). |
| 1001 | WER-SystemErrorReporting | "The computer has rebooted from a bugcheck": codice, parametri, percorso del dump. |
| 46 | volmgr | "Crash dump initialization failed": il dump non e stato possibile (paging/dump mal configurati). |

## Campi di Kernel-Power 41

`BugcheckCode` e `BugcheckParameter1..4` (valorizzati solo se il bugcheck e stato registrato), `PowerButtonTimestamp` (non zero se e stato premuto a lungo il pulsante), `SleepInProgress`, `Checkpoint`, `ConnectedStandbyInProgress` e altri.

## Interpretazione (Microsoft)

- 41 + 1001 + 6008 con codice: riavvio causato da bugcheck.
- `PowerButtonTimestamp` non zero: spegnimento forzato col pulsante.
- 41 con codici a zero, oppure 41 assente: il sistema non ha potuto scrivere nulla (alimentazione interrotta, blocco totale, reset di watchdog). Lo zero **non** prova un guasto dell'alimentatore.

## Conclusioni giustificate

"N arresti inattesi (41/6008) tra A e B." "Per l'arresto delle ore X, 1001 riporta il bugcheck 0xNNN."

## Conclusioni non giustificate

- Contare 41, 6008 e 1001 dello stesso arresto come tre incidenti.
- Attribuire la causa dalla sola severita Critical.
- Usare l'ora di registrazione di 41/6008 come ora dell'incidente.

## Ipotesi da verificare

Con codice: la categoria del bugcheck. Senza: alimentazione, blocco, watchdog; ognuna con evidenze indipendenti.

## Prossimi controlli

Tutti i campi di 41, 1001 e 1074 nello stesso periodo, dump disponibili, configurazione di paging e dump, log di UPS/alimentazione.

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart
- https://learn.microsoft.com/troubleshoot/windows-server/performance/troubleshoot-unexpected-reboots-system-event-logs
