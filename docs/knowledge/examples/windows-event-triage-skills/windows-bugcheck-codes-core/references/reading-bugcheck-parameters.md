# Leggere un bugcheck e i suoi parametri

**Quando si applica:** un evento o un dump riporta un codice (`0x...`) con quattro parametri.

## Dove si trovano

- Evento 1001 (System, WER-SystemErrorReporting): "The bugcheck was: 0xNNN (p1, p2, p3, p4)".
- Kernel-Power 41: campi `BugcheckCode` e `BugcheckParameter1..4` (zero se il codice non e stato registrato).
- WER 1001 (Application) con `Event Name: BlueScreen`: P1 e il codice, P2..P5 parametri.
- Dump (`MEMORY.DMP`, `Minidump`) analizzabile con `!analyze -v`.

## Come si interpreta

- Il **codice** dice quale controllo del kernel e fallito, non quale componente e difettoso.
- I **parametri** hanno significato diverso per ogni codice (indirizzo, tipo di errore, puntatore a record). Senza dump e simboli spesso non bastano a nominare un driver.
- Il **modulo citato** sulla schermata o in `!analyze` e quello in esecuzione al momento del controllo: puo essere la vittima di una corruzione causata altrove.
- Bugcheck **uguali ripetuti** rafforzano un'ipotesi; bugcheck **diversi** suggeriscono corruzione generica (memoria, driver) o hardware instabile.

## Conclusioni giustificate

"Bugcheck 0xNNN registrato alle ore X con parametri P." Il significato del codice secondo la documentazione Microsoft.

## Conclusioni non giustificate

- Nominare il componente guasto dal solo codice.
- Considerare un codice come "causa" invece che come categoria di fallimento.
- Dedurre il codice quando i campi sono zero o assenti.

## Ipotesi da verificare

Le cause elencate da Microsoft per un codice sono possibilita da verificare, non diagnosi.

## Prossimi controlli

Analisi del dump con simboli, elenco dei moduli caricati, Driver Verifier per sospetti driver, test di memoria, aggiornamenti recenti di driver e firmware, ripetizione o meno del codice.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-checks--blue-screens-
- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-code-reference2
