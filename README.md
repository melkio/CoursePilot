# CoursePilot

A GitHub template repository for bootstrapping new .NET solutions with a ready-to-use project layout, build/test workflows and Docker packaging.

## What's inside

- `src/CoursePilot.Host/` — the application host project.
- `test/CoursePilot.Tests/` — the NUnit test project.
- `CoursePilot.slnx` — the solution file.
- `Dockerfile` — multi-stage build for the host project.
- `.github/workflows/` — CI workflows (build, artifacts, clean-up).
- `rename-template.sh` — one-shot script that replaces every `CoursePilot` / `coursepilot` occurrence with your project name.

## How to bootstrap a new project from this template

After you create a new repository from this template (or clone it), follow these steps to turn it into your project.

Run the rename locally:

```bash
./rename-template.sh MyAwesomeApp
git checkout -b chore/initialize-MyAwesomeApp
git add -A
git commit -m "chore: initialize project as MyAwesomeApp"
git push -u origin chore/initialize-MyAwesomeApp
```

Then open a Pull Request from the pushed branch.

## Requirements

- .NET SDK matching the version declared in [.tool-versions](.tool-versions).
- `bash`, `sed`, `find` (already available on macOS, Linux and WSL).

The planned Users module requires the `Icaro.Domain` and `Icaro.MongoDB` packages from the
CodicePlastico GitHub Packages feed (`https://nuget.pkg.github.com/CodicePlastico/index.json`).
Restoring them requires an approved read-only credential with access to those packages. Keep
credentials in protected configuration or environment variables, not in the repository or logs.
The current project does not yet include these packages or the Users module.

## Build and test

```bash
dotnet build CoursePilot.slnx
dotnet test  CoursePilot.slnx
```
