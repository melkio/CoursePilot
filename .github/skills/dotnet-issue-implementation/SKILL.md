---
name: dotnet-issue-implementation
description: "Implement one existing GitHub issue by issue number in this .NET solution, following .NET best practices and recognized design patterns. Use for: implement issue, work on issue, gh issue, dotnet feature, clean architecture, SOLID, issue number, acceptance criteria, build and test verification."
argument-hint: "Provide the GitHub issue number to implement (e.g. 42)."
user-invocable: false
---

# .NET Issue Implementation

## When to Use
- A GitHub issue already exists and defines the scope to implement.
- The input is a single issue number.
- The work must be completed inside this repository with code, tests, build, and test verification.

## Required Input
- `issue-number`: integer, mandatory

If the input is missing or not numeric, stop and ask for it.

## Non-Negotiable Constraints
- Do not start coding before reading the full GitHub issue and confirming the scope.
- Do not modify the PRD, the issue body, or unrelated source files.
- Do not push branches, open pull requests, force-push, or run destructive git operations.
- Do not add dependencies, projects, or frameworks beyond what the issue requires; justify any new NuGet package.
- Do not introduce speculative abstractions, unused interfaces, or future-proof layers that are not required by the acceptance criteria.
- Do not bypass tests. Build and tests must pass before reporting completion.
- Only work on the scope defined in the referenced issue.

## Procedure

### Step 1 — Load the issue
1. Verify the GitHub CLI is available and authenticated:
   - `gh --version`
   - `gh auth status`
2. Fetch the issue:
   - `gh issue view <issue-number> --json number,title,body,labels,state,url`
3. If the issue does not exist, is closed, or is missing the `ready` label, stop and report the situation before doing anything else.
4. If the issue references a PRD path, read that PRD file for context.

### Step 2 — Understand the codebase impact
1. Inspect the solution layout: `CoursePilot.slnx`, `src/`, `test/`.
2. Locate the projects, namespaces, and existing patterns that the change must extend.
3. Identify analogous existing features to use as implementation templates.
4. Note the current target framework, nullability, and language version from the relevant `.csproj` files.

### Step 3 — Build an implementation plan
Use the `todo` tool to create a focused task list reflecting the issue's acceptance criteria.
The plan must include:
- production code changes
- unit tests covering each acceptance criterion
- build and test verification
- final reporting

Keep the plan minimal: one todo per acceptance criterion or per cohesive change.

### Step 4 — Implement
Apply these .NET conventions consistently:
- Respect the existing SDK style, `Nullable`, `ImplicitUsings`, target framework, and folder layout.
- Follow Microsoft naming guidelines.
- Prefer minimal public surface, `internal` by default, and immutable types where they simplify the model.
- Use async end-to-end where applicable, with `CancellationToken` on public async methods.
- Use the built-in DI container with deliberate service lifetimes.
- Use the Options pattern for configuration.
- Use guard clauses and specific exceptions.
- Use `ILogger<T>` with structured logging.
- For HTTP endpoints, stay consistent with the existing web style and return appropriate results.
- For persistence, align with the existing repository or data-access approach.

Apply design patterns only when they fit naturally:
- SOLID as the default lens
- Strategy for interchangeable algorithms
- Factory or Builder for non-trivial construction
- Decorator for cross-cutting behavior over existing abstractions
- Mediator or CQRS only if already established in the codebase
- Adapter at integration boundaries
- Result or Option styles for expected control-flow outcomes

### Step 5 — Tests
1. Add or extend tests in `test/CoursePilot.Tests/`.
2. Cover each acceptance criterion with at least one test.
3. Follow Arrange-Act-Assert.
4. Prefer `Theory` and `InlineData` for parameterized cases when appropriate.

### Step 6 — Verify
Run, in order, and stop on first failure:
1. `dotnet build CoursePilot.slnx`
2. `dotnet test CoursePilot.slnx`

If either fails, fix and rerun until both succeed.

### Step 7 — Final report
Return:
- Issue reference: `#<number> - <title> - <url>`
- Files created or modified, grouped by project
- Acceptance criterion to test mapping
- Design choices and patterns applied, with a one-line justification each
- Anything intentionally left out of scope and why
- Suggested commit message in the form `feat(#<number>): <short description>` or similar

If the issue is ambiguous, blocked by a missing dependency, or larger than declared, stop and report the blocker instead of guessing.
