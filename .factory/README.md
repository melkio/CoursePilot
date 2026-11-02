# Software Factory

Processo SDLC guidato da agenti AI. **Tutta l'interazione avviene su GitHub** (issue, PR, commenti, label);
lo stato del processo è dato dalle label `stage:*`. Un dispatcher (Hermes, ogni 5 minuti) osserva il repo
e avvia l'agente giusto.

| Fase | Trigger (label applicata da un utente autorizzato) | Agente / modello | Output |
|---|---|---|---|
| Discovery | `stage:discovery` | Analyst · GPT-6.1 Sol | domande nei commenti, iterativo |
| Consolidamento funzionale | `stage:discovery-approved` | Analyst | sezione *Functional specification* nel body, label → `stage:planning` |
| Planning | `stage:planning` (automatica) | Product manager · GPT-6.1 Sol | feature → `type:feature` + `stage:design`; epica → `type:epic` + issue figlie `type:feature` + `stage:design` |
| Design | `type:feature` **e** `stage:design` | Architect · GPT-6.1 Sol | proposta tecnica nei commenti, iterativo |
| Consolidamento tecnico | `stage:design-approved` | Architect | sezione *Tech details and plan* nel body |
| Implementazione | `stage:ready` | Copilot coding agent · GPT-6.1 Terra | PR collegata alla issue |
| Review | `stage:review` (sulla PR) | Reviewer avversariale · Claude Opus 5.5 | review con commenti inline |
| Lessons learned | `stage:review-completed` (sulla PR) | Lessons · GPT-6.1 Sol | PR che aggiorna `.github/copilot-instructions.md` |

## Epiche e feature

Il product manager valuta se la issue è implementabile in una singola PR. Se è un'epica:
- la suddivide in **slice verticali**: ogni issue figlia è completa funzionalmente e tecnicamente
  (dall'API ai test), rilasciabile e dimostrabile da sola — mai suddivisioni per layer;
- ogni figlia ha la label `type:feature`, la sua specifica funzionale, la sezione `Dependencies`
  (`Depends on #X`) e il riferimento `Part of #N`; è collegata come sub-issue dell'epica;
- il body dell'epica riporta la sezione `Linked issues` con l'ordine di implementazione.

Le epiche non passano dal design: ogni feature figlia entra direttamente in `stage:design`.

## Regole

- Si risponde alle domande degli agenti **con un nuovo commento**. Ogni nuovo commento fa ripartire l'agente.
- I commenti degli agenti contengono un marker nascosto `<!-- factory:<ruolo> -->`: non rimuoverlo.
- In fase di test solo `@melkio` può interagire: commenti e label `stage:*` di altri utenti vengono rimossi.
- Gli agenti non fanno mai merge e non approvano PR.

## File

- `analyst.md`, `product-manager.md`, `architect.md`, `reviewer.md`, `lessons.md` — prompt dei ruoli (modificabili via PR).
- `../.github/copilot-instructions.md` — regole di coding lette da Copilot, aggiornate dalla fase *lessons*.
