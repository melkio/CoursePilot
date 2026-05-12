---
name: create-gh-issue
description: "Create a single GitHub issue via the GitHub CLI and persist a local markdown file for it. Use when any agent needs to: create a GitHub issue from a structured title and body, apply labels, capture the assigned issue number, and write a local tracking file named <issue-number>.md inside a target folder. Keywords: gh issue create, create GitHub issue, local issue file, issue number, ready label, issue tracking."
argument-hint: "Title, body, target local folder, and optional extra labels for the issue to create."
user-invocable: false
---

# Create GitHub Issue

## When to Use
- Any agent needs to create one GitHub issue and track it locally.
- The caller has already prepared the issue title and body content.
- A local `<issue-number>.md` file must be created after the issue is opened on GitHub.

## Prerequisites
Before creating any issue, verify the GitHub environment:
1. Confirm `gh` is installed: `gh --version`
2. Confirm authentication: `gh auth status`
3. Confirm the working directory is inside a GitHub repository: `gh repo view`
If any of these fail, stop immediately and report what failed and why.

## Procedure

### Step 1 — Validate inputs
- Required: `title` (non-empty string)
- Required: `body` (markdown string using the [standard template](./assets/issue-template.md))
- Required: `target-folder` (path where `<issue-number>.md` will be written)
- Optional: additional labels beyond `ready`

### Step 2 — Write the body to a temporary file
Write the issue body to a temporary file, for example:
```
/tmp/gh-issue-body-<slug>.md
```
Use a unique name to avoid collisions when creating multiple issues in sequence.

### Step 3 — Create the GitHub issue
```bash
gh issue create \
  --title "<title>" \
  --body-file "/tmp/gh-issue-body-<slug>.md" \
  --label "ready"
```
Append `--label "<label>"` for each additional label if provided.

### Step 4 — Extract the issue number
`gh issue create` returns the URL of the created issue, for example:
```
https://github.com/<owner>/<repo>/issues/42
```
Parse the last path segment to obtain the issue number (`42` in the example).

### Step 5 — Remove the temporary file
```bash
rm /tmp/gh-issue-body-<slug>.md
```

### Step 6 — Create the local issue file
1. Create `<target-folder>/issues/` if it does not already exist.
2. Check that `<target-folder>/issues/<issue-number>.md` does not already exist. If it does, stop and report the conflict — do not overwrite.
3. Write the file with this header followed by the full issue body:

```md
# <title>

- **GitHub issue**: #<issue-number>
- **URL**: <full GitHub URL>

<full issue body>
```

## Output
Return exactly:
```
#<number> - <title> - <local path>
```
where `<local path>` is the absolute or relative path to the file created in step 6.

## Failure Handling
| Condition | Action |
|-----------|--------|
| `gh` not installed | Stop. Report: "`gh` CLI not found. Install from https://cli.github.com" |
| Not authenticated | Stop. Report: "Run `gh auth login` to authenticate" |
| Not a GitHub repo | Stop. Report the `gh repo view` error message |
| Body file write fails | Stop. Report the OS error and path |
| `gh issue create` fails | Stop. Report the command exit code and stderr |
| Local file already exists | Stop. Report the conflicting path and issue number |
