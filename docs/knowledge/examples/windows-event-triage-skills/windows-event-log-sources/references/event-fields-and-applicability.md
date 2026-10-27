# Leggere un evento: ID, payload, gravita, fonte, timestamp

**Quando si applica:** ogni volta che una conclusione si basa su un evento o su un conteggio di eventi.

## Gli elementi sono cinque, non uno

1. **Identita**: canale + provider + Event ID (+ versione). Lo stesso numero in provider diversi e un evento diverso (per esempio 1001 in Application/Windows Error Reporting e 1001 in System/WER-SystemErrorReporting). Un ID isolato non identifica un fenomeno.
2. **Payload** (EventData): i campi che contano (BugcheckCode, ErrorSource, exception code, nome del driver). Cita solo campi presenti; se il tool li ha troncati o minimizzati, dichiaralo.
3. **Gravita** (Level): Critical/Error/Warning/Information. E la gravita *scelta dal provider* per quel tipo di evento, non una misura dell'impatto reale. Kernel-Power 41 e Critical sempre; un errore WHEA corretto e Warning.
4. **Fonte e finestra**: quale tool, quale canale, quale intervallo. L'assenza di eventi dimostra solo che quella sorgente, in quella finestra, non li ha restituiti (controlla `complete`, `truncated`, `coverage`).
5. **Tempo**: ora di registrazione (TimeCreated) contro ora dell'incidente (vedi `windows-incident-correlation`).

## Condizioni di applicabilita

Una regola di interpretazione vale solo se i campi richiesti sono nel dato. Esempio: "BugcheckCode non zero implica bugcheck" richiede il campo; se l'evento e stato riassunto senza campi, non puoi applicarla.

## Conclusioni giustificate

Quelle che citano canale, provider, ID, ora e i campi usati.

## Conclusioni non giustificate

- Inventare campi non restituiti.
- Dedurre la gravita dell'impatto dal Level.
- Dire "nessun evento" quando la sorgente era parziale, troncata, non disponibile o non interrogata.

## Ipotesi da verificare

Qualunque spiegazione che richieda campi assenti.

## Prossimi controlli

Richiedere i dettagli del record, ampliare o restringere la finestra, interrogare la sorgente non coperta.

## Riferimenti

- https://learn.microsoft.com/windows/win32/wes/windows-event-log
