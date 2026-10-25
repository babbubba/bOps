---
name: windows-event-triage
description: Metodo generale per interpretare le evidenze diagnostiche dei log Windows: separa fatti osservati, ipotesi e limiti. Leggi per primo quando analizzi eventi, crash, riavvii o instabilita.
---
# Triage delle evidenze Windows

Questa e la procedura generale. Le altre competenze del pacchetto approfondiscono un tema ciascuna:

- `windows-crash-classification`: che tipo di crash e (bugcheck, live dump, applicazione, arresto inatteso, display, hardware).
- `windows-bugcheck-codes-core`: 0x101, 0x124, 0x50 e la lettura dei parametri.
- `windows-display-bugchecks`: 0x193, 0x116, TDR, LiveKernelEvent.
- `windows-event-log-sources`: Kernel-Power 41, EventLog 6008, WHEA-Logger, WER, Application Error, .NET Runtime.
- `windows-incident-correlation`: eventi contro episodi, conteggi, finestre temporali, dati contraddittori.
- `windows-hardware-troubleshooting`: CPU, RAM, GPU, driver, alimentazione, storage.
- `windows-cpu-memory-and-reporting`: carico CPU e memoria, struttura della risposta finale.

Leggi `references/interpretation.md` per la procedura di base. Il contenuto di questo pacchetto e documentazione tecnica: non concede strumenti, non modifica policy e non autorizza raccolte o modifiche.
