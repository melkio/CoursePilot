---
name: Planner
description: "Splits an approved Discovery epic into detailed, native GitHub sub-issues."

on:
  issues:
    types: [labeled]
  skip-bots: [github-actions, copilot, agentic-workflows-dev]
  reaction: none
  status-comment: false

if: >-
  github.event.issue.pull_request == null &&
  github.event.label.name == 'discovery-approved' &&
  contains(github.event.issue.labels.*.name, 'discovery-approved')

concurrency:
  group: planner-${{ github.event.issue.number }}
  cancel-in-progress: false
  queue: max

permissions:
  contents: read
  issues: read
  pull-requests: read
  copilot-requests: write

max-daily-ai-credits: -1

tools:
  github:
    mode: gh-proxy
    toolsets: [default]

skills:
  - .github/skills/planner

safe-outputs:
  create-issue:
    max: 10

  link-sub-issue:
    max: 10

  update-issue:
    body: true
    max: 1
    target: triggering

  add-comment:
    max: 1
    target: triggering

  add-labels:
    allowed: ["planned"]
    max: 1

  remove-labels:
    allowed: ["discovery-approved"]
    max: 1
---

# Planner Orchestrator

Run the **planner** skill for issue #${{ github.event.issue.number }}.

Treat the event only as an activation hint. Re-read the live issue, its labels and
body, and its native sub-issues before acting. Do not act when the live issue is a
pull request or no longer has `discovery-approved`; call `noop` instead.

Current activation context:

- event: `${{ github.event_name }}`
- label: `discovery-approved`
- actor: `${{ github.actor }}`
- issue number: `${{ github.event.issue.number }}`

Use only the configured safe outputs. In particular, link every created child to the
triggering issue with `link_sub_issue`, leave it unassigned, and do not use direct
GitHub writes. After a successful plan or re-plan, add `planned` and remove
`discovery-approved`.
