# Conteggi aggregati e conteggi per categoria

**Quando si applica:** un tool riporta un totale (per esempio "eventi critici/errori") e altri riportano categorie specifiche (live dump, WHEA, crash applicativi).

## Regole

- Un **aggregato** puo gia contenere le categorie: i conteggi non vanno sommati senza sapere cosa include il totale.
- Un conteggio per **categoria** vale per la sua definizione (provider, ID, filtro) e finestra; non e comparabile con un altro filtro.
- Se il totale e *minore* della somma delle categorie, c'e sovrapposizione o finestre/filtri diversi: non sono errori da "correggere", sono un limite da dichiarare.
- Riporta ogni numero con **sorgente, filtro e finestra**.

## Evidenze da cercare

Definizione del filtro di ogni conteggio, finestra, eventuale troncamento (`truncated`, `complete`), categorie annidate (per esempio i LiveKernelEvent contati anche come "errori hardware").

## Conclusioni giustificate

"Il tool A riporta N (filtro F, ultimi 180 giorni); il tool B riporta M per la categoria C nella stessa finestra." Sovrapposizioni dichiarate.

## Conclusioni non giustificate

- Un unico "numero totale di problemi" ottenuto sommando aggregati e categorie.
- Confrontare conteggi di finestre diverse come se fossero omogenei.
- Presentare una percentuale tra numeri con definizioni diverse.

## Ipotesi da verificare

Che un totale includa o escluda una categoria: verifica la definizione o richiedi un conteggio con filtri espliciti.

## Prossimi controlli

Rieseguire la raccolta con filtri equivalenti, o elencare gli episodi e contarli una volta.

## Riferimenti

- https://learn.microsoft.com/windows/win32/wes/windows-event-log
