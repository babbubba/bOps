# Arresti inattesi, blocchi e riavvii

**Quando si applica:** Kernel-Power 41, EventLog 6008, "riavvii anomali", schermo nero, sistema bloccato.

## Cosa registra Windows

- Kernel-Power 41 (critical): "The system has rebooted without cleanly shutting down first." Descrive il *sintomo*: blocco, crash o perdita di alimentazione. La gravita critical non indica la causa.
- EventLog 6008: "The previous system shutdown at <ora> on <data> was unexpected." Registrato al riavvio; l'ora nel testo e quella dell'arresto precedente.
- Normale: 1074 (arresto avviato da processo/utente) e 6006 (servizio EventLog fermato regolarmente).
- Bugcheck: 1001 (WER-SystemErrorReporting) con codice e parametri; poi 41 con `BugcheckCode` non zero.

## Come distinguere gli scenari (Microsoft)

1. **Bugcheck**: 41 con `BugcheckCode` valorizzato, 1001, dump presenti.
2. **Pulsante di accensione premuto a lungo**: 41 con `PowerButtonTimestamp` non zero.
3. **Blocco totale o perdita di alimentazione**: 41 assente, oppure con `BugcheckCode` e `PowerButtonTimestamp` a zero. Il sistema non ha avuto modo di scrivere i codici. Puo indicare problemi di alimentazione, ma anche un blocco hardware, un reset da watchdog (ASR, heartbeat Hyper-V) o un bugcheck che non ha potuto scrivere il dump.
4. Se manca il dump, controlla un evento volmgr 46 ("Crash dump initialization failed"): la configurazione del file di paging puo impedire il dump.

## Conclusioni giustificate

- "Riavvio senza arresto pulito alle ore X" (41/6008).
- "Associato a bugcheck 0xNNN" solo con 1001 o `BugcheckCode` valorizzato.

## Conclusioni non giustificate

- "Guasto dell'alimentatore" con `BugcheckCode` zero: lo zero dice che *mancano* informazioni, non che l'alimentatore sia guasto.
- "Surriscaldamento" senza dati termici.
- Contare 41 e 6008 come due incidenti: sono due registrazioni dello stesso arresto.

## Ipotesi da verificare

Con codici zero: alimentazione, blocco hardware, reset da watchdog, errore di storage durante la scrittura del dump. Ciascuna richiede evidenza indipendente.

## Prossimi controlli

Dettagli originali di 41 (tutti i campi), presenza di 1001 e 1074 vicino, configurazione del dump e del paging, log di alimentazione/UPS, sensori e firmware (fuori dalla portata di questa procedura se non forniti da uno strumento).

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart
- https://learn.microsoft.com/troubleshoot/windows-server/performance/troubleshoot-unexpected-reboots-system-event-logs
