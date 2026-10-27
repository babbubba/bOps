# Finestre temporali e timestamp

**Quando si applica:** confronto tra periodi ("ultimi 180 giorni" contro "ultimi 7 giorni"), o tra l'ora di un evento e quella dell'incidente.

## Finestre

- Confronta solo finestre uguali oppure normalizza (per giorno, per settimana) e dichiaralo.
- Una finestra piu breve non "smentisce" una piu lunga: ne e un sottoinsieme.
- Una finestra puo essere limitata dalla **retention** del registro (log ruotati): l'assenza di eventi antichi non prova che non siano accaduti.

## Timestamp

- Registrazione contro incidente: Kernel-Power 41 e 6008 sono scritti **al riavvio successivo**; il testo di 6008 contiene l'ora dell'arresto precedente. WER 1001 e scritto dopo la creazione del rapporto. Un LiveKernelEvent riporta l'ora del rapporto.
- Fuso orario: UTC contro ora locale; confronta nello stesso riferimento. Cambi di ora legale possono spostare di un'ora.
- Se un tool riporta solo data senza ora, non ordinare eventi dello stesso giorno.

## Evidenze da cercare

Campo `TimeCreated` (UTC), ora nel testo del messaggio, differenza tra eventi dello stesso arresto, limiti reali della finestra restituita.

## Conclusioni giustificate

"Alle ore X (UTC) e registrato l'evento Y; l'arresto riportato risale alle ore Z." Intervalli calcolati in modo esplicito.

## Conclusioni non giustificate

- Ordinare cause ed effetti dall'ora di registrazione.
- Dichiarare "prima/dopo" tra eventi senza ora o con fusi diversi.
- Trasformare una finestra breve in un trend.

## Ipotesi da verificare

Una tendenza (peggioramento/miglioramento) richiede finestre confrontabili e piu punti.

## Prossimi controlli

Riallineare i fusi, ampliare la finestra, verificare la retention, raccogliere l'ora dell'incidente dal dettaglio del record.

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart
