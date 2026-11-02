# Software Factory

Processo SDLC guidato da agenti AI. **Tutta l'interazione avviene su GitHub** (issue, PR, commenti, label);
lo stato del processo è dato dalle label `stage:*`. Un dispatcher (Hermes, ogni 5 minuti) osserva il repo
e avvia l'agente giusto.

| Fase | Trigger (label applicata da un utente autorizzato) | Agente / modello | Output |
|---|---|---|---|
| Discovery | `stage:discovery` | Analyst · GPT-6.1 Sol | domande nei commenti, iterativo |
| Consolidamento funzionale | `stage:discovery-approved` | Analyst | sezione *Functional specification* nel body, label → `stage:planning` |
| Planning | `stage:planning` (automatica) | Product manager · GPT-6.1 Sol | feature → `type:feature` + `stage:design`; epica → `type:epic` + issue figlie `type:feature` + `stage:design` (senza dipendenze) o `stage:blocked` (con dipendenze) |
| Sblocco | dipendenze chiuse (automatico) | Dispatcher, senza LLM | `stage:blocked` → `stage:design` |
| Design | `type:feature` **e** `stage:design` | Architect · GPT-6.1 Sol | proposta tecnica nei commenti, iterativo |
| Consolidamento tecnico | `stage:design-approved` | Architect | sezione *Tech details and plan* nel body + SHA di `master` su cui si basa il design |
| Verifica del design | `stage:ready`, se `master` ha toccato file del piano dopo il design | Architect · GPT-6.1 Sol | piano aggiornato, oppure ritorno a `stage:design` |
| Implementazione | `stage:ready` (design ancora valido) | Copilot coding agent · GPT-6.1 Terra | PR collegata alla issue |
| Review | `stage:review` (sulla PR) | Reviewer avversariale · Claude Opus 5.5 | review con commenti inline |
| Lessons learned | `stage:review-completed` (sulla PR) | Lessons · GPT-6.1 Sol | PR che aggiorna `.github/copilot-instructions.md` |

## Epiche e feature

Il product manager valuta se la issue è implementabile in una singola PR. Se è un'epica:
- la suddivide in **slice verticali**: ogni issue figlia è completa funzionalmente e tecnicamente
  (dall'API ai test), rilasciabile e dimostrabile da sola — mai suddivisioni per layer;
- ogni figlia ha la label `type:feature`, la sua specifica funzionale, la sezione `Dependencies`
  (`Depends on #X`) e il riferimento `Part of #N`; è collegata come sub-issue dell'epica;
- il body dell'epica riporta la sezione `Linked issues` con l'ordine di implementazione.

Le epiche non passano dal design.

## Dipendenze e design sequenziale

Il design tecnico di una feature deve basarsi sul codice che le sue dipendenze avranno già introdotto
(componenti comuni, modelli, infrastruttura), altrimenti l'architect pianificherebbe di reimplementarli.

- Il product manager dichiara le dipendenze **funzionali e tecniche**: se una slice riusa componenti introdotti
  da un'altra, ne dipende anche se funzionalmente indipendente.
- Le figlie senza dipendenze nascono in `stage:design`; le altre in `stage:blocked`.
- Il dispatcher sposta automaticamente una feature da `stage:blocked` a `stage:design` quando **tutte** le issue
  in `Depends on #X` sono chiuse (PR mergiata). Una dipendenza da un'epica è soddisfatta quando tutte le sue
  sub-issue sono chiuse.
- Le feature indipendenti tra loro procedono in parallelo.
- Il design registra lo SHA di `master` su cui è stato fatto. Se al momento di `stage:ready` su `master` sono
  arrivati commit che toccano file previsti dal piano, l'architect lo riverifica: se è ancora valido aggiorna lo
  SHA e si procede con Copilot, altrimenti propone le modifiche e riporta la issue in `stage:design`.

## Regole

- Si risponde alle domande degli agenti **con un nuovo commento**. Ogni nuovo commento fa ripartire l'agente.
- I commenti degli agenti contengono un marker nascosto `<!-- factory:<ruolo> -->`: non rimuoverlo.
- In fase di test solo `@melkio` può interagire: commenti e label `stage:*` di altri utenti vengono rimossi.
- Gli agenti non fanno mai merge e non approvano PR.

## File

- `analyst.md`, `product-manager.md`, `architect.md`, `reviewer.md`, `lessons.md` — prompt dei ruoli (modificabili via PR).
- `../.github/copilot-instructions.md` — regole di coding lette da Copilot, aggiornate dalla fase *lessons*.
