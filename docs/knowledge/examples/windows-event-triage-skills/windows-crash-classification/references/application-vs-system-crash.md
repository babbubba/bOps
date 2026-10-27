# Crash di applicazione contro crash di sistema

**Quando si applica:** eventi del canale Application (1000, 1001, 1002, .NET Runtime 1026) letti insieme a eventi System.

## Evidenze da cercare

- **Application Error 1000** (Error): applicazione e modulo che hanno fallito, codice eccezione, offset, ID processo. E l'evento del crash di *un processo*.
- **Windows Error Reporting 1001**: riassunto del rapporto; il nome evento dice di che fault si tratta (APPCRASH per applicazioni, BlueScreen per bugcheck, LiveKernelEvent per live dump).
- **Application Hang 1002**: il processo non risponde, non e necessariamente terminato.
- **.NET Runtime 1026**: eccezione non gestita con stack, correla con 1000 tramite processo e tempo.

## Interpretazione tecnica

- Un crash applicativo non riavvia il sistema: nessun 41/6008 e implicito.
- Il modulo "faulting" e spesso una DLL Microsoft (ntdll, kernelbase, ucrtbase): sono *vittime* di corruzione precedente. Il nome del modulo da solo non dimostra il difetto.
- Codici comuni: 0xC0000005 accesso non valido, 0xC0000374 heap corrotto, 0xC0000409 fail-fast, 0xE0434352 eccezione CLR.

## Conclusioni giustificate

"L'applicazione X ha avuto N crash con eccezione Y". "Il processo e terminato, il sistema operativo no."

## Conclusioni non giustificate

- Attribuire un crash di processo a RAM/GPU/alimentatore.
- Collegare un crash di processo a un riavvio solo per vicinanza di tempo.
- Dire che il "runtime" o un modulo di sistema ha causato il crash per provider o ID.

## Ipotesi da verificare

Bug dell'applicazione, plugin/driver di terze parti, aggiornamenti recenti, corruzione di memoria (se crash multipli in processi diversi con eccezioni di memoria).

## Prossimi controlli

Dump di processo con WER/GFlags, versione dell'applicazione, cronologia degli aggiornamenti, conteggio per processo e per codice eccezione, eventuali riavvii separati.

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior
- https://learn.microsoft.com/windows/win32/wer/windows-error-reporting
