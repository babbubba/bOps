# Eventi contro episodi, cluster ravvicinati

**Quando si applica:** qualunque conteggio ("N errori", "N crash", "N eventi WHEA").

## Concetti

- **Evento**: una riga del registro.
- **Episodio** (o incidente): un fenomeno reale che puo produrre piu eventi, anche di fonti diverse.
- **Cluster**: eventi dello stesso tipo e componente molto ravvicinati (secondi o pochi minuti). Spesso un errore persistente continua a segnalarsi finche dura la condizione.

## Evidenze da cercare

- Intervalli tra eventi consecutivi dello stesso provider/ID/componente.
- Raffiche (decine di eventi in pochi secondi) separate da lunghi silenzi.
- Un riavvio o un cambio di stato (avvio, aggiornamento driver) tra un gruppo e il successivo.

## Interpretazione

Una raffica di N eventi e di norma **un episodio con N registrazioni**, non N guasti indipendenti. Il numero di eventi misura il volume di registrazione; il numero di episodi misura le volte in cui il fenomeno si e manifestato. Riporta entrambi quando li hai, con la regola di raggruppamento (per esempio "eventi a meno di 5 minuti l'uno dall'altro").

## Conclusioni giustificate

"150 eventi in 3 episodi (gruppi separati da oltre X minuti)." "Il volume e concentrato in pochi istanti."

## Conclusioni non giustificate

- "150 guasti" o "150 crash" da 150 righe.
- Dire che sono "indipendenti" senza prova di separazione.
- Usare il numero di eventi come misura di gravita senza considerare la natura (corretto/fatale).

## Ipotesi da verificare

Che una condizione persistente (calore, driver, firmware) alimenti la raffica; che il gruppo sia terminato per un cambiamento.

## Prossimi controlli

Istogramma per ora/giorno, intervalli, confronto con riavvii e aggiornamenti, e stessa analisi per le altre categorie.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/whea/error-processing
