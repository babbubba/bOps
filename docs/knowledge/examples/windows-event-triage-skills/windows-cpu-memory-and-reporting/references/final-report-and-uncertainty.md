# Relazione finale: fatti, ipotesi, limiti

**Quando si applica:** ogni risposta diagnostica conclusiva.

## Struttura consigliata

1. **Fatti osservati**: cosa hanno restituito gli strumenti, con sorgente, finestra e conteggi (episodi, non solo righe).
2. **Interpretazione**: cosa significano quei fatti secondo la documentazione, tipo di evento incluso (BSOD, live dump, crash applicativo, arresto inatteso).
3. **Ipotesi**: spiegazioni possibili, ciascuna con cio che la sosterrebbe o la smentirebbe. Usa un linguaggio proporzionale: "compatibile con", "non dimostrato".
4. **Limitazioni**: sorgenti parziali, troncate o non disponibili; finestre diverse; dati mancanti; eventi esclusi.
5. **Prossimi controlli**: azioni di sola raccolta o verifica, mirate, che discriminano le ipotesi. Non vengono eseguite dalla relazione.

## Regole di calibrazione

- Una causa e "confermata" solo quando i dati la sostengono direttamente.
- Un componente hardware non si nomina come guasto senza evidenze ripetute e indipendenti.
- Non sommare categorie diverse; non contare piu volte lo stesso episodio.
- Non attribuire gli eventi corretti agli eventi fatali.
- Se i dati contraddicono, riporta la contraddizione.
- Distingui "non rilevato" (sorgente interrogata, nessun evento) da "non verificabile" (sorgente parziale o non interrogata).

## Conclusioni giustificate

Quelle ancorate a un campo, a un conteggio o a una definizione documentata, con sorgente e finestra.

## Conclusioni non giustificate

Nomi di componenti da sostituire, cause "probabili" presentate come fatti, rassicurazioni ("tutto ok") su dati incompleti.

## Ipotesi da verificare

Ogni ipotesi dichiarata come tale, con il dato che la sosterrebbe o la smentirebbe.

## Prossimi controlli

Proponi 2-4 verifiche concrete, ordinate dalla piu economica e reversibile, e dichiara quale ipotesi ciascuna discrimina.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-checks--blue-screens-
- https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart
