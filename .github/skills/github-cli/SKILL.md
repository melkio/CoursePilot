---
name: github-cli
description: "Thin wrapper around the GitHub CLI (`gh`). Invoke this skill whenever any agent needs to run a `gh` command. Currently documents `gh issue create`. Keywords: gh, GitHub CLI, gh issue create, create GitHub issue, ready label, issue number."
argument-hint: "Pick the gh operation to run (today: issue-create) and pass its required parameters."
user-invocable: false
---

# GitHub CLI

A thin, single-responsibility wrapper around the `gh` command-line tool. This skill knows only how to invoke `gh` and report its results. It does NOT define content templates, language rules, label policies, local-file conventions, or any agent-level workflow.

## Prerequisites
Before invoking any `gh` command, verify the environment:
1. `gh --version` — confirm the CLI is installed.
2. `gh auth status` — confirm authentication.
3. `gh repo view` — confirm the working directory is inside a GitHub repository.

If any check fails, stop immediately and report exactly which check failed and the command's stderr.

## Operations

### `gh issue create`

#### Inputs
- `title` (string, required): issue title as provided by the caller.
- `body` (string, required): full markdown body as provided by the caller.
- `labels` (list of strings, optional): labels to apply. May be empty.

#### Procedure
1. **Write the body to a temporary file.** Use a unique name to avoid collisions when creating multiple issues in sequence:
   ```
   /tmp/gh-issue-body-<unique-suffix>.md
   ```
2. **Run the command:**
   ```bash
   gh issue create \
     --title "<title>" \
     --body-file "/tmp/gh-issue-body-<unique-suffix>.md"
   ```
   Append `--label "<label>"` once per entry in `labels`.
3. **Parse the output.** On success, `gh issue create` prints the URL of the created issue, for example:
   ```
   https://github.com/<owner>/<repo>/issues/42
   ```
   Extract the trailing path segment as the issue number (`42` in the example).
4. **Remove the temporary file:**
   ```bash
   rm /tmp/gh-issue-body-<unique-suffix>.md
   ```
   Remove the temp file even if the command failed.

#### Output
Return exactly:
```
{ "issue-number": <integer>, "url": "<full GitHub URL>" }
```

#### Failure Handling
| Condition | Action |
|-----------|--------|
| `gh` not installed | Stop. Report: "`gh` CLI not found. Install from https://cli.github.com" |
| Not authenticated | Stop. Report: "Run `gh auth login` to authenticate" |
| Not a GitHub repo | Stop. Report the `gh repo view` error message |
| Temp body-file write fails | Stop. Report the OS error and the path |
| `gh issue create` fails | Stop. Report the command exit code and stderr; still remove the temp file |
| URL parsing fails | Stop. Report the raw `gh` output |
