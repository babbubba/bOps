# Application Error, WER, Hang e .NET Runtime

**Quando si applica:** crash di processi nel canale Application.

## Eventi

- **Application Error 1000** (Error): "Faulting application name ... Faulting module name ... Exception code ... Fault offset ... Faulting process id ...". E il crash del processo.
- **Windows Error Reporting 1001** (Information): rapporto; `Event Name` indica il tipo (APPCRASH, BlueScreen, LiveKernelEvent, ...) e P1..Pn la firma del problema.
- **Application Hang 1002**: il processo non risponde.
- **.NET Runtime 1026**: eccezione .NET non gestita, con descrizione e stack.

## Interpretazione

- Le coppie 1000 + 1001 (e 1026 per .NET) descrivono lo stesso crash: un incidente, non tre.
- Il modulo guasto e spesso una DLL Microsoft (ntdll.dll, kernelbase.dll, kernel32.dll): le DLL molto usate sono "catturate" come vittime di corruzione precedente. Il nome da solo non dimostra la causa.
- Eccezioni comuni: 0xC0000005 (violazione di accesso), 0xC0000374 (corruzione dell'heap), 0xC0000022 (accesso negato), 0xC0000409 (fail-fast), 0xC00000FD (stack overflow), 0xE0434352 (eccezione CLR non gestita).

## Conclusioni giustificate

"Il processo P e terminato N volte con eccezione E." "I record 1000/1001/1026 dello stesso istante rappresentano un solo crash."

## Conclusioni non giustificate

- Attribuire il crash a un componente hardware o al runtime/modulo di sistema.
- Dedurre un'eccezione da .NET 1026 se manca messaggio e stack.
- Collegare un crash di processo a un riavvio per vicinanza temporale.

## Ipotesi da verificare

Bug applicativo, estensioni o hook di terze parti, antivirus, aggiornamenti recenti, corruzione di memoria (se crash in processi diversi con eccezioni di memoria).

## Prossimi controlli

Dump del processo con WER/GFlags, versione dell'app e delle DLL, cronologia degli aggiornamenti, correlazione per ID processo e tempi.

## Riferimenti

- https://learn.microsoft.com/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior
- https://learn.microsoft.com/troubleshoot/developer/webapps/iis/site-behavior-performance/process-termination-crash
