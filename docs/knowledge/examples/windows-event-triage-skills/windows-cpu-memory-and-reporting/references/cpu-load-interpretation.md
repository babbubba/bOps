# Interpretare il carico CPU

**Quando si applica:** richieste del tipo "controlla CPU e memoria e dimmi se vedi anomalie".

## Evidenze da cercare

- Utilizzo totale e per processore (se disponibile), media su un intervallo, picchi, coda di esecuzione.
- I processi che consumano di piu e il loro peso relativo; processi di sistema contro applicativi.
- Il contesto: un campione istantaneo e una fotografia, non un trend.

## Interpretazione

- Un picco breve, un carico alto durante un'attivita legittima (aggiornamenti, indicizzazione, scansioni, compilazioni) non e un'anomalia di per se.
- Utilizzo alto *costante* senza attivita nota, un processo inatteso, o un solo core saturo con il resto a riposo meritano un approfondimento.
- Il carico del processo diagnostico stesso va escluso.
- Il carico CPU non spiega bugcheck o riavvii.

## Conclusioni giustificate

"Nel campione X la CPU e al N% con i processi P1..Pk come maggiori consumatori; nessuna saturazione prolungata osservata." Includi la durata del campione.

## Conclusioni non giustificate

- "Nessuna anomalia" da un solo campione breve: dichiara il limite.
- "Anomalia" da un valore alto momentaneo.
- Collegare il carico a instabilita storica.

## Ipotesi da verificare

Che un processo sia la causa di carico persistente; che il picco sia ricorrente: servono campioni ripetuti.

## Prossimi controlli

Campionamento su un intervallo, attribuzione per processo, confronto con l'attivita pianificata, conteggio dei thread/handle se utile.

## Riferimenti

- https://learn.microsoft.com/sysinternals/resources/windows-internals
