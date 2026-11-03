# Coding instructions for CoursePilot

These rules are read by the software factory developer agent (and by AI assistants in the IDE). They are maintained by the software factory
(see `.factory/README.md`) and grow from review findings.

## Workflow
- Implement exactly the issue's *Functional specification* and *Tech details and plan*. If the plan is
  wrong or incomplete, follow the spec, keep the change minimal, and explain the deviation in the PR.
- Reference the issue in the PR description (`Fixes #N`) and map each acceptance criterion (AC1…) to the
  tests that cover it.
- Keep PRs focused: no unrelated refactors or formatting changes.

## Stack
- .NET 10 (`net10.0`, see `.tool-versions`), ASP.NET Core, solution `CoursePilot.slnx`.
- Tests: NUnit (`test/CoursePilot.Tests`). Build and test with `dotnet build CoursePilot.slnx` and
  `dotnet test CoursePilot.slnx`; both must pass before opening the PR.

## Code
- Modules follow `.github/skills/hexagonal-module-design/SKILL.md` (Hexagonal Architecture with Icaro):
  layout under `src/CoursePilot.Host/Modules/<Plural>/`, naming, repository/service/notifier, Mongo adapters,
  transports, commands. Inter-module calls only through `I<OtherModule>Service`.
- Nullable reference types are enabled: no `!` suppressions without a comment explaining why.
- Validate external input at the boundary; return proper HTTP status codes and ProblemDetails for errors.
- No secrets or environment-specific values in code; use configuration (`appsettings*.json`, env vars).
- Prefer small, single-purpose types and methods; name things after domain concepts.

## Tests
- Every acceptance criterion has at least one test whose name references it.
- Test behaviour, not implementation details; avoid asserting on log text or private state.
- Remove placeholder tests (e.g. `AlwaysTrue`) when real tests are added.
