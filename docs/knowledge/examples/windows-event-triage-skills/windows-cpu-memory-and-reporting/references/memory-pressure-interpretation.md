# Interpretare la memoria: RAM, commit, paginazione, perdite

**Quando si applica:** richieste su stato della memoria, "memoria alta", lentezza, perdite di memoria.

## Concetti

- **RAM utilizzata** non e un problema in se: Windows usa la RAM libera per cache. Conta la memoria *disponibile* e la pressione.
- **Commit**: memoria promessa (RAM + file di paging). Un commit vicino al limite e un rischio concreto.
- **Paginazione**: i page fault "soft" sono normali; i letture da disco ("hard faults") frequenti con poca memoria disponibile indicano pressione.
- **Perdita di memoria**: crescita monotona di un processo (o del pool kernel) nel tempo, non un valore alto isolato.

## Evidenze da cercare

Memoria disponibile e percentuale in uso, commit contro limite, uso del file di paging, working set dei processi maggiori, andamento nel tempo, eventi di esaurimento (risorse insufficienti).

## Conclusioni giustificate

"Disponibile X GB su Y; commit al N%; processi maggiori P1..Pk." "Nessuna pressione evidente nel campione."

## Conclusioni non giustificate

- "Memoria quasi piena" dal solo valore "in uso".
- "Perdita di memoria" da un unico campione.
- Collegare memoria alta a guasto di RAM: sono domini diversi (capacita contro integrita).

## Ipotesi da verificare

Perdita in un processo o driver, carico legittimo che richiede piu RAM, file di paging troppo piccolo.

## Prossimi controlli

Campioni ripetuti, crescita per processo, pool kernel, configurazione del paging, salute dei moduli (test di memoria) solo se ci sono sintomi di corruzione.

## Riferimenti

- https://learn.microsoft.com/sysinternals/resources/windows-internals
