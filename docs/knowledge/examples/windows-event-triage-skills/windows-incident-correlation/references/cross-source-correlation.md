# Correlare fonti e strumenti diversi senza doppio conteggio

**Quando si applica:** si hanno risultati di piu strumenti (stabilita, crash, eventi di sistema, hardware) sullo stesso periodo.

## Un incidente, piu registrazioni

Un bugcheck puo comparire come: evento 1001 (System), Kernel-Power 41, EventLog 6008, WER 1001 `BlueScreen` (Application), un dump in `Minidump`, una voce nell'Affidabilita. Sono **rappresentazioni dello stesso episodio**. Un crash di processo: Application Error 1000 + WER 1001 + .NET Runtime 1026.

## Procedura

1. Per ogni categoria, elenca gli episodi con ora stimata dell'incidente.
2. Abbina le registrazioni che condividono identificatori o dettagli compatibili (codice bugcheck, ID processo, nome applicazione, report ID) e tempi vicini.
3. Conta ogni episodio una volta; riporta quali fonti lo hanno visto.
4. Se una fonte vede un episodio che un'altra non vede, e un fatto di copertura, non una contraddizione.
5. Diversi tipi di evento (live dump, bugcheck, crash di app, arresto inatteso) restano categorie separate, anche se contemporanee.

## Conclusioni giustificate

"L'episodio del giorno X e documentato da 41, 6008 e 1001 (bugcheck 0xNNN)." Il totale dei soli episodi dedotti con regola esplicita.

## Conclusioni non giustificate

- Sommare i conteggi di strumenti che descrivono gli stessi episodi.
- Abbinare per sola vicinanza quando mancano identificatori e dichiararlo come certo.
- Attribuire una causa comune a categorie diverse solo perche vicine nel tempo.

## Ipotesi da verificare

Che due categorie condividano la causa (per esempio TDR e live dump grafici): richiede componente comune e ripetizione.

## Prossimi controlli

Dettagli originali dei record per gli identificatori, coverage di ogni sorgente, finestre allineate.

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-server/performance/troubleshoot-unexpected-reboots-system-event-logs
