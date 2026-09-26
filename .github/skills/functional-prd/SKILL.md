---
name: functional-prd
description: "Run a structured functional discovery interview one question at a time, then generate a PRD in docs/<NNN-feature-slug>/PRD.md. Use for feature analysis, product requirements, PRD generation, functional requirements discovery, intervista funzionale, analisi funzionale, requisito funzionale. No technical details, no source code changes."
argument-hint: "Describe the feature to analyze from a functional point of view."
user-invocable: false
---
# Functional PRD Discovery

## When to Use
- A rough feature idea needs to be fleshed out functionally before any implementation begins.
- A PRD document must be created under `docs/<NNN-slug>/PRD.md`.

## Interview Procedure
1. Start from the user's rough feature description.
2. Identify the single most important unresolved functional ambiguity from the checklist below.
3. Ask the user one concise question.
4. Wait for the answer before asking the next question.
5. If a question can be answered by reading the codebase, read the codebase instead of asking.
6. Continue until all dimensions in the checklist are clear, or the user explicitly accepts remaining open items as deferred.
7. Only then generate the PRD file following [Naming Rules](./references/naming-rules.md) and the [PRD Template](./references/prd-template.md).

## Interview Checklist
Cover all of the following before declaring the interview complete:
- Problem or opportunity
- Target users and actors
- User goals and expected outcomes
- Trigger conditions and entry points
- Main user flows
- Business rules and validations
- Edge cases and exception paths
- Permissions or role differences
- Success criteria
- Non-goals and exclusions
- Assumptions and unresolved items

## Constraints During the Interview
- Ask exactly one question per message.
- Stay at the functional and business level; avoid technical wording.
- Match the user's conversation language when practical.
- When reading the codebase, use it only to understand domain language, existing workflows, constraints, and terminology. Do not turn findings into a technical analysis.

## After Creating the PRD
Report briefly:
- The final feature title
- The path of the generated PRD
- Any explicitly unresolved open questions

## Stop Condition
Done only when:
- the PRD file has been created under `docs/<NNN-slug>/PRD.md`, and
- remaining open questions, if any, are explicitly marked as deferred by the user or captured in the PRD's Open Questions section.
