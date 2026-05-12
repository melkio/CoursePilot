# DotNetTemplate

A GitHub template repository for bootstrapping new .NET solutions with a ready-to-use project layout, build/test workflows and Docker packaging.

## What's inside

- `src/DotNetTemplate.Host/` — the application host project.
- `test/DotNetTemplate.Tests/` — the xUnit test project.
- `DotNetTemplate.slnx` — the solution file.
- `Dockerfile` — multi-stage build for the host project.
- `.github/workflows/` — CI workflows (build, artifacts, clean-up).
- `rename-template.sh` — one-shot script that replaces every `DotNetTemplate` / `dotnet-template` occurrence with your project name.

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

## Build and test

```bash
dotnet build DotNetTemplate.slnx
dotnet test  DotNetTemplate.slnx
```
