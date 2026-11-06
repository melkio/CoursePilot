# Software Factory

Processo SDLC guidato da agenti AI. **Tutta l'interazione avviene su GitHub** (issue, PR, commenti, label);
lo stato del processo è dato dalle label `stage:*`. Il dispatcher deterministico è un workflow GitHub Actions:
gli eventi GitHub danno risposta immediata e una riconciliazione schedulata ogni 5 minuti recupera eventi mancanti
(best effort, non SLA). L'automazione resta disattivata finché non viene completato manualmente il cutover da Hermes.

| Fase | Trigger (label applicata da un utente autorizzato) | Agente / modello | Output |
|---|---|---|---|
| Discovery | `stage:discovery` | Analyst · GPT-6.1 Sol | domande nei commenti, iterativo |
| Consolidamento funzionale | `stage:discovery-approved` | Analyst | sezione *Functional specification* nel body, label → `stage:planning` |
| Planning | `stage:planning` (automatica) | Product manager · GPT-6.1 Sol | feature → `type:feature` + `stage:design`; epica → `type:epic` + issue figlie `type:feature` + `stage:design` (senza dipendenze) o `stage:blocked` (con dipendenze) |
| Sblocco | dipendenze chiuse (automatico) | Dispatcher, senza LLM | `stage:blocked` → `stage:design` |
| Design | `type:feature` **e** `stage:design` | Architect · GPT-6.1 Sol | proposta tecnica nei commenti, iterativo |
| Consolidamento tecnico | `stage:design-approved` | Architect | sezione *Tech details and plan* nel body + SHA di `master` su cui si basa il design |
| Verifica del design | `stage:ready`, se `master` ha toccato file del piano dopo il design | Architect · GPT-6.1 Sol | piano aggiornato, oppure ritorno a `stage:design` |
| Implementazione | `stage:ready` (design ancora valido) | Developer · GPT-6.1 Terra | branch `feat/<n>-<slug>`, build e test verdi, PR con `Fixes #n` |
| Review | `stage:review` (sulla PR) | Reviewer avversariale · Claude Opus 5.5 | review con commenti inline (rieseguita a ogni nuovo commit) |
| Follow-up | commento con `/factory <istruzioni>` sulla PR | Developer · GPT-6.1 Terra | commit sullo stesso branch, risposta punto per punto |
| Lessons learned | `stage:review-completed` (sulla PR) | Lessons · GPT-6.1 Sol | PR che aggiorna `.github/copilot-instructions.md` |

## Epiche e feature

Il product manager valuta se la issue è implementabile in una singola PR. Se è un'epica:
- la suddivide in **slice verticali**: ogni issue figlia è completa funzionalmente e tecnicamente
  (dall'API ai test), rilasciabile e dimostrabile da sola — mai suddivisioni per layer;
- ogni figlia ha la label `type:feature`, la sua specifica funzionale, la sezione `Dependencies`
  (`Depends on #X`) e il riferimento `Part of #N`; è collegata come sub-issue dell'epica;
- il body dell'epica riporta la sezione `Linked issues` con l'ordine di implementazione.

Le epiche non passano dal design.

## Implementazione, review e fix

L'implementazione è eseguita dal developer della factory (non da Copilot coding agent), così l'intero processo è
riproducibile fuori da GitHub: GitHub è solo lo storage.

1. `stage:ready` → il developer crea `feat/<n>-<slug>`, implementa il piano, esegue `dotnet build` e `dotnet test`
   finché sono verdi e apre la PR (`Fixes #n`, mappa criterio di accettazione → test).
2. `stage:review` sulla PR → review avversariale (modello diverso da chi ha scritto il codice).
3. `/factory <istruzioni>` in un commento sulla PR (conversazione, inline o review), come `@copilot` → il developer
   esegue sullo stesso branch e risponde punto per punto. Il dispatcher mette 👀 sul commento quando lo prende in
   carico e 🚀 quando ha finito. Se la PR è in `stage:review`, il reviewer rivede il nuovo commit.
   Una PR `[WIP]` riporta in `## Remaining work` cosa manca: si riprende solo con un comando `/factory`.
4. `stage:review-completed` → lessons learned; il merge resta manuale.

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
  SHA e si procede con l'implementazione, altrimenti propone le modifiche e riporta la issue in `stage:design`.

## Regole

- Si risponde alle domande degli agenti **con un nuovo commento**. Ogni nuovo commento fa ripartire l'agente.
- I commenti degli agenti contengono un marker nascosto `<!-- factory:<ruolo> -->`: non rimuoverlo.
- In fase di test solo `@melkio` può interagire: commenti e label `stage:*` di altri utenti vengono rimossi.
- Gli agenti non fanno mai merge e non approvano PR.

## Runtime GitHub Actions

Il prerequisito trasversale è implementato in `.github/workflows/factory-dispatcher.yml`,
`.github/actions/factory-runtime/` e `.github/scripts/factory/`. Non contiene prompt né implementa i ruoli:
i prompt `.factory/*.md` rimangono la fonte del comportamento; gli step #55–#65 forniscono i rispettivi workflow.
Il dispatcher invoca solo workflow presenti nel default branch e dichiara `blocked` se lo step non è ancora installato.

| Evento | Risorse candidate | Filtro |
|---|---|---|
| `issues` (opened, labeled, closed, reopened) | issue | label `stage:*` valida e stato aperto; chiusure/riaperture attivano la riconciliazione |
| `issue_comment:created` | issue o PR | nuovo commento di `melkio`; sulle issue riprende discovery/design, sulle PR richiede `/factory` |
| `pull_request` (opened, reopened, labeled, synchronize, closed) | PR | `stage:review` per review; PR da fork mai passate a uno step con secret |
| `pull_request_review_comment:created` | PR | comando `/factory` di `melkio`, solo su review inviata |
| `pull_request_review:submitted` | PR | comando `/factory` di `melkio`; review pending ignorate |
| `schedule` (ogni 5 minuti) | risorse aperte attive | riconciliazione paginata e dispatch per singola risorsa |
| `workflow_dispatch` | repository o risorsa indicata | ripresa manuale; in test solo `melkio` e solo workflow selezionato dal default branch |

Durante il test, l'identità ammessa è l'account `melkio`. Commenti e aggiunte `stage:*` di altri autori
vengono rimossi; un marker `<!-- factory:... -->` non autentica l'autore. Gli output tecnici vengono preservati
solo se l'API GitHub restituisce l'identità bot `github-actions[bot]` (login e tipo), non in base al solo testo.
Le label `stage:*` devono essere mutuamente esclusive; `stage:review*` è valida solo sulle PR.

Il contratto JSON versionato (schema 1) contiene repository, issue/PR, step, tipo/ID/revisione input, SHA atteso,
chiave/checkpoint e risultato `waiting`, `completed`, `blocked` o `failed`. Il checkpoint è conservato come
commento tecnico nascosto sulla risorsa, attribuito al bot verificato; i cursori dei commenti sono paginati per
recuperare comandi arrivati durante una run. Dispatcher e workflow di step devono condividere il gruppo
`factory-resource-${{ github.repository }}-${{ fromJSON(inputs.contract).resource.number }}` con
`cancel-in-progress: false`,
mentre risorse diverse possono avanzare in parallelo. Il dispatch successivo è esplicito via REST API e usa
sempre il default branch: nessuna transizione generata da `GITHUB_TOKEN` dipende da un altro webhook.
Alla prima attivazione, il dispatcher manuale con `initialize_only=true` crea i checkpoint e posiziona i cursori
alla fine degli input esistenti senza avviare step; sulle nuove risorse i cursori partono dall'applicazione della
label di fase, così i commenti successivi restano recuperabili.

Ogni workflow di step dovrà accettare il contratto condiviso, avviare il runtime (`start`), verificarlo prima
della pubblicazione (`verify`), registrare l'esito anche in failure handler (`result`) e richiedere esplicitamente
il passo successivo (`next`). Il runtime rilegge issue/PR, label, body, input e head SHA; un input cambiato non
può essere pubblicato come corrente. `start` espone `should-skip=true` se quella chiave è già attiva o completata:
il workflow deve saltare gli effetti. Retry e replay riusano la stessa chiave, quindi i publisher di ogni step devono essere
idempotenti. Non registrare prompt, commenti, diff o token nei log/summary.

### Discovery

`stage:discovery` avvia `.github/workflows/factory-discovery.yml` dal dispatcher; è possibile anche avviarlo
manualmente dal default branch fornendo il contratto condiviso. Il workflow rilegge l'issue e la conversazione
completa, paginata da GitHub, e carica `.factory/analyst.md` dal default branch trusted. La risposta viene pubblicata
solo come commento `<!-- factory:analyst -->`: body e label non vengono modificati e l'approvazione resta manuale.

Per rispondere, aggiungere **un nuovo commento** mentre la issue conserva `stage:discovery`; editare un commento non
riavvia l'intervista. Il dispatcher esclude PR, bot e autori non autorizzati. La concorrenza per risorsa serializza
le esecuzioni; commenti umani arrivati durante un run restano nel checkpoint e vengono processati al termine.
Errori CLI/API lasciano il checkpoint `failed`, senza commento di successo; dopo aver corretto il problema, riprendere
dal dispatcher manuale indicando il numero della issue. L'analyst invita l'owner ad applicare
`stage:discovery-approved` quando i criteri sono testabili, ma non applica la label né consolida il body.

### Permessi e secret

| Job | Permessi GitHub | Secret |
|---|---|---|
| Dispatcher | `contents: read`, `issues: write`, `pull-requests: write`, `actions: write` | solo `GITHUB_TOKEN` con permessi del job |
| Inferenza Copilot | nessun permesso di scrittura; accesso ai soli dati necessari | `COPILOT_GITHUB_TOKEN`, mai `GITHUB_TOKEN` usato come token Copilot |
| Publisher | permesso minimo necessario (`issues: write`/`pull-requests: write`; `actions: write` solo per dispatch) | `GITHUB_TOKEN`; mai il secret Copilot |

La CLI è installata con versione npm fissata `@github/copilot@1.0.106` e chiamata in modalità non interattiva
`copilot -p ... --model ... -s --no-ask-user`. Gli ID previsti sono `gpt-6.1-sol`, `gpt-6.1-terra` e
`claude-opus-5.5`; il modello viene passato esplicitamente, non si fa fallback automatico. I grant strumenti
vanno concessi per step con il minimo necessario; `--allow-all-tools` non è ammesso come scorciatoia.

Per l'inferenza configurare il secret Actions `COPILOT_GITHUB_TOKEN` con un token supportato da Copilot CLI.
Un fine-grained PAT deve appartenere all'account personale e avere il permesso account **Copilot Requests**;
sono supportati anche i token OAuth Copilot CLI e GitHub App user-to-server. I classic PAT `ghp_` non sono
supportati. Limitare repository/accesso e durata del token secondo policy, autorizzare l'uso CLI/modelli nella
licenza/policy dell'organizzazione, verificare costi/premium requests e scadenza, e ruotare/revocare il token
dopo l'uso o alla scadenza. La CLI e le sue richieste consumano quota; non eseguire smoke test senza approvazione.

### Smoke test manuale

Il workflow `.github/workflows/factory-smoke.yml` dimostra su runner GitHub-hosted installazione fissata,
autenticazione non interattiva, selezione del modello e pubblicazione controllata. Eseguirlo dalla scheda Actions
su `master`, autenticati come `melkio`, indicando il numero di una issue aperta creata da `melkio` e un modello.
Ripetere il preflight manuale per ciascun modello atteso prima del cutover, per non presumere disponibilità o
accesso uniforme a Sol, Terra e Opus. Il test richiede solo una risposta non vuota e non confronta testo specifico.
La risposta del modello non viene pubblicata: un job separato,
con solo `GITHUB_TOKEN`, aggiunge un commento fisso e deduplicato. Secret mancante, token non supportato,
modello non disponibile o output inatteso fanno fallire visibilmente la run senza commento di successo.

Verifiche locali senza secret:

```sh
node --test .github/scripts/factory/*.test.mjs
for file in .github/scripts/factory/*.mjs; do node --check "$file"; done
```

Il test Actions con token reale non è stato eseguito in questo ambiente; va autorizzato manualmente prima del cutover.

### Cutover, recovery e rollback

1. Lasciare `FACTORY_AUTOMATION_ENABLED` non impostata/false: eventi e schedule non avviano step automatici.
   Il workflow manuale è limitato a `melkio`; lo smoke test resta separato.
2. Verificare licenza/policy Copilot, secret dedicato, permessi Actions, protezioni branch e workflow dei singoli step.
   Inventariare le issue/PR già in `stage:*`: il checkpoint iniziale migra i cursori, non presume che Hermes abbia
   completato uno step; mantenere solo le fasi che si desidera realmente riprendere.
3. Congelare temporaneamente nuovi commenti/label di processo e avviare manualmente il dispatcher con
   `initialize_only=true`; verificare che abbia solo inizializzato i checkpoint, senza dispatch di step.
4. **Disabilitare Hermes e verificare che non abbia più scheduler/dispatcher attivi**, poi impostare la variabile
   repository `FACTORY_AUTOMATION_ENABLED=true` e riaprire l'interazione. Non abilitare automazioni senza controllo umano.
5. Applicare una sola label di fase autorizzata e verificare run, checkpoint, SHA e output. Un errore CLI/API lascia
   il risultato `failed`/`blocked`, non promuove label e non dichiara successo.
6. Per riprendere: correggere il prerequisito, poi usare `workflow_dispatch` del dispatcher (numero risorsa opzionale);
   i cursori e le chiavi idempotenti in GitHub riprocessano input non completati. Se un job è cancellato, `waiting`
   non equivale a completato: la riconciliazione verifica la run, registra failure e ritenta lo stesso input.
7. Per rollback impostare la variabile a `false` (e disabilitare il workflow se richiesto), quindi ripristinare Hermes
   solo dopo aver verificato che il dispatcher Actions sia fermo. Non cancellare i commenti di checkpoint durante
   una recovery; non fare reset automatico delle label o merge.

Lo schedule è best effort. La concorrenza Actions non è una coda: la fonte di recovery sono i cursori/checkpoint
persistiti e il replay paginato. La cancellazione non equivale a completamento. Nessuno step fa merge, approva PR,
assegna issue a Copilot coding agent o attiva autonomamente la review. I workflow `build.yml` e `artifacts.yml`
non fanno parte del cutover e restano invariati.

## File

- `../.github/skills/` — skill del team con le convenzioni di progettazione (es. `hexagonal-module-design`),
  usate dall'architect per il design, dal developer per l'implementazione e dal reviewer per la verifica.

- `analyst.md`, `product-manager.md`, `architect.md`, `reviewer.md`, `lessons.md` — prompt dei ruoli (modificabili via PR).
- `developer.md` — prompt del developer (implementazione e fix).
- `../.github/copilot-instructions.md` — regole di coding lette dal developer (e dagli assistenti negli IDE), aggiornate dalla fase *lessons*.
