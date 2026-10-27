# Processore: quando i dati puntano alla CPU

**Quando si applica:** 0x101, errori WHEA di processore (machine check), blocchi di un core, riavvii sotto carico.

## Evidenze che indicano la CPU (ognuna da sola e debole)

- WHEA-Logger *fatale* con componente "Processor Core" o machine check (0x124 con P1 = 0), ripetuto sullo stesso APIC ID/banco.
- 0x101 ripetuto sullo stesso indice di processore.
- Errori WHEA *corretti* di processore in aumento nel tempo (tendenza, non prova).
- Instabilita che scompare riportando il firmware ai valori predefiniti (verifica dell'operatore).

## Evidenze che NON bastano

- Un singolo errore corretto, un singolo 0x101, un riavvio senza codice.
- Carico alto di un processo senza errori hardware.
- Il nome "Processor Core" in un evento corretto: e la sorgente che ha rilevato e corretto l'errore.

## Cause alternative da tenere aperte

Alimentazione/VRM, temperature, impostazioni di overclock o undervolt, firmware/microcode, memoria, driver che trattengono un core.

## Conclusioni giustificate

"Gli eventi indicano problemi riportati dal processore (tipo, frequenza, core)." La dimensione del fenomeno (episodi, non righe).

## Conclusioni non giustificate

"CPU da sostituire", "CPU difettosa" senza ripetizione compatibile e senza esclusione di impostazioni e alimentazione.

## Ipotesi da verificare

Instabilita del processore o della sua alimentazione; si distingue con test controllati: valori di default, stress test, confronto tra core, temperature.

## Prossimi controlli

Record WHEA completi (APIC ID, banco), BIOS/microcode, rimozione di overclock, temperature sotto carico, confronto con altri componenti.

## Riferimenti

- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x101---clock-watchdog-timeout
- https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x124---whea-uncorrectable-error
