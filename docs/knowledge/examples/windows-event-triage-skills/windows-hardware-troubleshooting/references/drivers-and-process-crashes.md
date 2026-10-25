# Driver e crash di processo

**Quando si applica:** un modulo viene citato in bugcheck o crash; piu applicazioni terminano; aggiornamenti recenti.

## Driver

- Il driver nominato in un bugcheck e quello in esecuzione al momento del controllo. Se compare in dump diversi e coerenti e un buon sospetto; se compare una volta, o e un modulo di sistema, no.
- Driver Verifier puo restringere un sospetto (e aggiunge carico: si usa su pochi driver).
- Controlla la data di installazione/aggiornamento: l'inizio dei problemi dopo un aggiornamento e una correlazione, non una prova.

## Processi

- Crash di *un* processo con eccezione ripetuta: spesso bug di quel software o dei suoi componenti.
- Crash di *molti* processi con eccezioni di memoria: puo indicare corruzione di memoria (software o hardware).
- Le DLL di sistema citate come "faulting module" sono spesso vittime.

## Conclusioni giustificate

"Il modulo X compare in N dump/eventi; l'ultimo aggiornamento di X risale a D."

## Conclusioni non giustificate

"Il driver X ha causato il crash" dal solo nome in un evento isolato; "Windows e corrotto" senza prova.

## Ipotesi da verificare

Bug del driver o del software, conflitti tra software, corruzione di memoria o del file system.

## Prossimi controlli

Confronto di piu dump, Driver Verifier sul sospetto, aggiornamento o rollback (decisione dell'operatore), controllo dei file di sistema, conteggio per processo/eccezione.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x50--page-fault-in-nonpaged-area
- https://learn.microsoft.com/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior
