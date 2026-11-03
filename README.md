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

## Build and test

```bash
dotnet build CoursePilot.slnx
dotnet test  CoursePilot.slnx
```

## Users slice

Users uses Icaro 1.0.0 and MongoDB. Configure the `codiceplastico` NuGet source at
`https://nuget.pkg.github.com/CodicePlastico/index.json` with an approved read-only
credential. NuGet accepts the protected environment variable
`NuGetPackageSourceCredentials_codiceplastico` containing
`Username=<username>;Password=<token>;ValidAuthenticationTypes=Basic`; never commit it.
The CI workflow expects `REGISTRY_TOKEN`. Integration tests require MongoDB and use
`MONGODB_URI` (default `mongodb://127.0.0.1:27017`). They create and remove isolated databases.

Set `MongoDB__ConnectionString` to a MongoDB URI including the database name (for example,
`mongodb://localhost:27017/CoursePilot`) for the application. In production,
set `DataProtection__KeysPath` to persistent shared key storage with restricted access and
appropriate at-rest protection, so cookies remain valid across instances and restarts.
Use HTTPS: authentication cookies are Secure in production; development explicitly allows
local HTTP and uses SameAsRequest. Set `Authentication__CookieHours` to change the default
8-hour non-sliding session, and `RateLimits__IdentityPermitLimit` /
`RateLimits__IdentityWindowMinutes` to configure registration/login limits.

Provision the first admin through the operator-only CLI, with `BootstrapAdmin__Email` and
`BootstrapAdmin__Password` supplied by protected configuration or environment variables
(not command-line arguments, files in the repository, or logs):

```bash
dotnet run --project src/CoursePilot.Host -- bootstrap-admin
dotnet run --project src/CoursePilot.Host
```

Email is required and normalized using trim and invariant uppercase; passwords must be
8–128 characters. Registration accepts only `Student` or `Teacher` (the latter starts
`Pending`), never `Admin`; approval is a separate admin operation. The API provides
`GET /api/users/antiforgery`, `POST /api/users/register`, `POST /api/users/login`,
`POST /api/users/logout`, `GET /api/users/me`, and `GET /api/users/{id}`. Admins have
`POST /api/admin/users/search` with JSON filters (`offset`, `limit`, `role`, `approval`), `GET /api/admin/users/{id}`, `PUT .../{id}/email`,
`PUT .../{id}/role`, and `POST .../{id}/approve-teacher`. Admin writes require `If-Match`
with the quoted integer version from the ETag (for example `"1"`); stale versions are 412.

All mutating requests, including register/login/logout, require the antiforgery cookie and
`X-CSRF-TOKEN` header obtained from the token endpoint. Obtain a fresh token after login,
logout, or any identity change. Swagger is enabled in development at `/swagger`, and its
mutating operations provide a field for the request token. Copy the `token` returned by
`GET /api/users/antiforgery` into `X-CSRF-TOKEN` before each mutation; refresh it after
login or logout. A safe demo sequence is: get token; register
Teacher; login Teacher; get token after login; inspect `/me`; login Admin in a separate cookie
session; get token; read teacher detail and ETag; approve using that ETag; verify the old
Teacher cookie is rejected; get a fresh token and log the Teacher in again; inspect `/me`.

For image builds, provide the same NuGet credentials through a BuildKit secret named
`nuget_credentials` (not a Docker build argument). The artifacts workflow currently does
not supply this secret, so its image build cannot succeed until credentials are configured
in a later change. For example, with the credential stored in the environment variable
`NuGetPackageSourceCredentials_codiceplastico`:

```bash
docker build --secret id=nuget_credentials,env=NuGetPackageSourceCredentials_codiceplastico -t coursepilot:local .
```
