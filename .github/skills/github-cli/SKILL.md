---
name: github-cli
description: "Thin wrapper around the GitHub CLI (`gh`). Invoke this skill whenever any agent needs to run a `gh` command. Documents: gh issue create, gh issue view, gh issue comment. Keywords: gh, GitHub CLI, gh issue create, gh issue view, gh issue comment, create GitHub issue, fetch issue, post comment, ready label, issue number."
argument-hint: "Pick the gh operation to run (issue-create, issue-view, issue-comment) and pass its required parameters."
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

---

### `gh issue view`

#### Inputs
- `issue-number` (integer, required): the issue to fetch.
- `fields` (list of strings, optional): JSON fields to include. Defaults to `title,body,labels,comments`.

#### Procedure
1. **Run the command:**
   ```bash
   gh issue view <issue-number> --json <comma-separated-fields>
   ```
   Default fields when none are specified: `title,body,labels,comments`.
2. **Parse the JSON output** and return it as-is to the caller.

#### Output
Return the parsed JSON object. Example shape:
```json
{
  "title": "...",
  "body": "...",
  "labels": [{"name": "..."}],
  "comments": [{"body": "...", "author": {"login": "..."}}]
}
```

#### Failure Handling
| Condition | Action |
|-----------|--------|
| Issue not found | Stop. Report the `gh` stderr (issue number may be wrong) |
| `gh issue view` fails | Stop. Report the command exit code and stderr |

---

### `gh issue comment`

#### Inputs
- `issue-number` (integer, required): the issue to comment on.
- `body` (string, required): full markdown body of the comment as provided by the caller.

#### Procedure
1. **Write the body to a temporary file:**
   ```
   /tmp/gh-issue-comment-<issue-number>-<unique-suffix>.md
   ```
2. **Run the command:**
   ```bash
   gh issue comment <issue-number> --body-file "/tmp/gh-issue-comment-<issue-number>-<unique-suffix>.md"
   ```
3. **Parse the output.** On success, `gh issue comment` prints the URL of the new comment.
4. **Remove the temporary file** even if the command failed.

#### Output
Return exactly:
```
{ "comment-url": "<full GitHub comment URL>" }
```

#### Failure Handling
| Condition | Action |
|-----------|--------|
| Issue not found | Stop. Report the `gh` stderr |
| Temp file write fails | Stop. Report the OS error and the path |
| `gh issue comment` fails | Stop. Report the command exit code and stderr; still remove the temp file |
