---
name: Product Discovery
description: "Runs an asynchronous human-in-the-loop product discovery interview on explicitly enabled issues."

on:
  issues:
    types: [labeled, assigned]
  issue_comment:
    types: [created]
  skip-bots: [github-actions, copilot, agentic-workflows-dev]
  reaction: none
  status-comment: false

# The issue must currently be discovery-enabled and have at least one assignee.
# Listening to both `labeled` and `assigned` makes activation independent of
# whether the label or the PO assignment is applied first.
if: >-
  github.event.issue.pull_request == null &&
  github.event.issue.assignee != null &&
  contains(github.event.issue.labels.*.name, 'discovery-enabled') &&
  (
    github.event_name == 'issue_comment' ||
    github.event.action == 'assigned' ||
    github.event.label.name == 'discovery-enabled' ||
    github.event.label.name == 'approve-discovery'
  )

# Serialize every event for the same issue without locking the issue itself.
# Humans remain free to comment while the agent is running; later events queue.
concurrency:
  group: product-discovery-${{ github.event.issue.number }}
  cancel-in-progress: false
  queue: max

permissions:
  contents: read
  issues: read
  pull-requests: read
  copilot-requests: write

tools:
  github:
    toolsets: [default]

skills:
  - .github/skills/product-discovery

safe-outputs:
  add-comment:
    max: 1
    target: triggering

  update-issue:
    body:
    max: 1
    required-labels: ["discovery-enabled"]
    target: "*"

  add-labels:
    allowed: ["discovery approved"]
    max: 1

  remove-labels:
    allowed: ["discovery-enabled", "approve-discovery", "discovery approved"]
    max: 3
---

# Product Discovery Orchestrator

Run the **product-discovery** skill for issue #${{ github.event.issue.number }}.

Treat the event payload only as an activation hint. Always re-read the live issue,
its current labels, assignees, body, and complete comment history before acting.
This matters because multiple human events may have queued while a previous run
was executing.

Current activation context:

- event: `${{ github.event_name }}`
- actor: `${{ github.actor }}`
- triggering comment id, when present: `${{ github.event.comment.id }}`
- issue number: `${{ github.event.issue.number }}`

Use the skill to choose exactly one mode:

1. **START_OR_RESUME** — `discovery-enabled` was applied, or an assignee was added
   to an already-enabled issue.
2. **INTERVIEW_TURN** — a human created an issue comment while Discovery is active.
3. **APPROVE** — the `approve-discovery` label was applied.
4. **NOOP** — the live issue state shows that this event is stale, already processed,
   not actionable, or Discovery is no longer active.

Important implementation rules:

- The `discovery-enabled` label is persistent while Discovery is active.
- The issue must have at least one assignee. In v1, current assignees are the
  Discovery Owners; only an assignee may approve Discovery.
- Keep the complete conversation in normal issue comments.
- Keep the current structured state in the issue body using `update_issue` with
  `operation: replace-island`. Never replace the human-authored issue body.
- When calling `update_issue`, always pass the live issue number and only update
  an issue that still has `discovery-enabled`.
- Use one agent comment per turn at most.
- On approval, finalize the snapshot, add `discovery approved`, and remove both
  `approve-discovery` and `discovery-enabled`.
- If an unauthorized user applies `approve-discovery`, do not finalize. Remove only
  `approve-discovery` and explain briefly that approval must come from an assignee.
- If `discovery-enabled` is applied to a previously approved issue, reopen Discovery
  as the next revision and remove `discovery approved`.
