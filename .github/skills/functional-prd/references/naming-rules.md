# PRD Folder Naming Rules

## Location
All PRD documents live under `docs/`. Create `docs/` lazily if it does not already exist.

## Folder Format
```
docs/<NNN-slug>/PRD.md
```

- `NNN` — zero-padded three-digit integer, e.g. `001`, `002`, `042`.
- `slug` — English, lowercase, kebab-case title derived from the **finalized** feature name.

## How to Compute NNN
1. List all folders directly under `docs/` whose names match the pattern `^[0-9]{3}-`.
2. Extract the numeric prefix from each matching folder name.
3. Increment the highest found value by one.
4. If no numbered folders exist yet, start at `001`.

## Rules
- Always treat each request as a new feature. Never update or reuse an existing PRD folder.
- The slug must be derived from the finalized feature title, not from the user's rough initial description.
- Keep the slug to 3–6 meaningful words.
