# Rumore della diagnostica, dati insufficienti e contraddizioni

**Quando si applica:** eventi che potrebbero essere generati dalla raccolta stessa, sorgenti incomplete, risultati che non concordano.

## Eventi prodotti dalla diagnostica

La raccolta puo lasciare tracce: avvio di processi di interrogazione, accessi a WMI/registro, crash del tool, voci operative dei provider interrogati, creazione di report. Prima di contare un evento come "problema della macchina":

- Confronta l'ora con l'inizio dell'analisi: eventi successivi all'avvio dell'analisi o del processo diagnostico sono sospetti.
- Verifica processo, utente e componente: un evento il cui processo e lo strumento diagnostico non descrive lo stato della macchina.
- Dichiara l'esclusione e il criterio usato.

## Dati insufficienti

- `partial`, `truncated`, `unavailable`, `coverage` limitata: la conclusione vale solo per la parte coperta.
- Campi mancanti o minimizzati: non ricostruirli.
- Una sorgente non interrogata non e una sorgente "pulita".

## Dati contraddittori

- Verifica prima finestre, filtri, definizioni e fusi orari: spesso la contraddizione sparisce.
- Se resta, riportala come tale con entrambe le fonti e indica la verifica che la risolverebbe.
- Non scegliere la versione "piu comoda".

## Conclusioni giustificate

"Con le sorgenti A e B (complete) emerge X; la sorgente C era parziale quindi Y non e valutabile."

## Conclusioni non giustificate

- Riempire un vuoto con la spiegazione piu plausibile.
- Includere nel totale eventi riconducibili alla raccolta.
- Ignorare un avviso di troncamento nella risposta finale.

## Ipotesi da verificare

Che un picco coincida con la fase di diagnostica; che una sorgente mancante contenga eventi rilevanti.

## Prossimi controlli

Rieseguire le sorgenti parziali con finestre piu strette, raccogliere i dettagli dei record sospetti, ripetere dopo la fine della diagnostica.

## Riferimenti

- https://learn.microsoft.com/windows/win32/wes/windows-event-log
