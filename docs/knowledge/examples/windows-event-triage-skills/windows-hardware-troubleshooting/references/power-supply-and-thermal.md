# Alimentazione e temperature

**Quando si applica:** riavvii o spegnimenti senza codice, Kernel-Power 41 con zeri, sospetto alimentatore o surriscaldamento.

## Cosa dicono (e non dicono) i dati di Windows

- Kernel-Power 41 con `BugcheckCode` zero e `PowerButtonTimestamp` zero, o 41 assente: il sistema non ha avuto modo di scrivere i codici. Microsoft indica fra le possibilita: alimentazione interrotta o insufficiente, batteria rimossa, blocco con spegnimento forzato.
- Questi dati **non misurano** tensioni, potenze o temperature. Senza sensori, la diagnosi di alimentatore o calore e un'ipotesi.

## Evidenze indipendenti utili (se fornite)

Temperature di CPU/GPU e throttling, velocita dei ventilatori, log di UPS o batteria, eventi di alimentazione (batteria, interruzioni), picchi di consumo, eventi di spegnimento termico del firmware, riavvii sotto carico riproducibili.

## Conclusioni giustificate

"I dati sono compatibili con una perdita di alimentazione o un blocco, ma non lo dimostrano." "Temperature e tensioni non sono state fornite."

## Conclusioni non giustificate

"Alimentatore guasto", "surriscaldamento" o "problema di alimentazione" come cause confermate senza dati indipendenti.

## Ipotesi da verificare

Alimentatore insufficiente dopo l'aggiunta di componenti; interruzioni di rete elettrica; surriscaldamento; reset da watchdog. Ognuna con la sua prova.

## Prossimi controlli

Sensori e log del firmware, prova con alimentazione alternativa (decisione dell'operatore), UPS, pulizia e ventilazione, riproduzione sotto carico.

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart
- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x124---whea-uncorrectable-error
