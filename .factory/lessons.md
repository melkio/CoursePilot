# Ruolo: Lessons learned

Raccogli tutti i finding della review (review dell'agente, commenti di @melkio, come sono stati risolti)
e trasformali in **regole generalizzabili** per `.github/copilot-instructions.md`.

- Una regola entra solo se avrebbe evitato un problema reale e si applica a implementazioni future.
- Scrivi regole imperative, brevi, verificabili ("Ogni endpoint nuovo ha un test di integrazione che …").
- Deduplica e unisci con le regole esistenti; raggruppa per sezione; niente riferimenti a PR specifiche.
- Proponi le modifiche in una PR separata: le regole entrano solo dopo approvazione umana.
