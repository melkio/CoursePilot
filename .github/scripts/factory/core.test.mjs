import assert from "node:assert/strict";
import test from "node:test";

import {
  CLI_VERSION,
  inputMatchesContract,
  makeContract,
  MODELS,
  pendingInputs,
  parseFactoryCommand,
  resourceMatchesContract,
  routeEvent,
  shouldSkipDispatch,
  validateContract,
} from "./core.mjs";

const repo = "melkio/CoursePilot";

function issue(labels, overrides = {}) {
  return {
    number: 12,
    state: "open",
    updated_at: "2026-11-06T10:00:00Z",
    labels: labels.map((name) => ({ name })),
    ...overrides,
  };
}

function pull(labels, overrides = {}) {
  return {
    ...issue(labels, { number: 34 }),
    head: { sha: "a".repeat(40), repo: { full_name: repo } },
    ...overrides,
  };
}

const melkio = { login: "melkio", type: "User" };
const factoryBot = { login: "github-actions[bot]", type: "Bot" };
const copiedMarkerUser = { login: "untrusted", type: "User" };

test("AC4: pins the CLI and the exact models used by factory prompts", () => {
  assert.equal(CLI_VERSION, "v1.0.106");
  assert.deepEqual(Object.values(MODELS), [
    "gpt-6.1-sol",
    "gpt-6.1-terra",
    "claude-opus-5.5",
  ]);
});

test("AC1: routes only supported issue and PR stages to their own step", () => {
  const discovery = routeEvent("issues", {
    action: "labeled",
    sender: melkio,
    issue: issue(["stage:discovery"]),
    label: { name: "stage:discovery", id: 1 },
  }, repo);
  assert.equal(discovery.step, "discovery");
  assert.equal(discovery.resource.type, "issue");

  const review = routeEvent("pull_request", {
    action: "labeled",
    sender: melkio,
    pull_request: pull(["stage:review"]),
    label: { name: "stage:review", id: 2 },
  }, repo);
  assert.equal(review.step, "review");
  assert.equal(review.resource.type, "pull_request");
  assert.equal(review.expected.sha, "a".repeat(40));

  const designOnEpic = routeEvent("issues", {
    action: "labeled",
    sender: melkio,
    issue: issue(["type:epic", "stage:design"]),
    label: { name: "stage:design", id: 3 },
  }, repo);
  assert.equal(designOnEpic.action, "ignore");
});

test("AC1: excludes unauthorized actors, copied markers, and factory output loops", () => {
  const unauthorizedLabel = routeEvent("issues", {
    action: "labeled",
    sender: copiedMarkerUser,
    issue: issue(["stage:discovery"]),
    label: { name: "stage:discovery", id: 4 },
  }, repo);
  assert.equal(unauthorizedLabel.action, "remove-label");

  const botLabel = routeEvent("issues", {
    action: "labeled",
    sender: factoryBot,
    issue: issue(["stage:planning"]),
    label: { name: "stage:planning", id: 5 },
  }, repo);
  assert.equal(botLabel.action, "ignore");

  const botComment = routeEvent("issue_comment", {
    action: "created",
    issue: issue(["stage:discovery"]),
    comment: {
      id: 77,
      body: "<!-- factory:analyst --> /factory run again",
      user: factoryBot,
    },
  }, repo);
  assert.equal(botComment.action, "ignore");

  const humanCopiedMarker = routeEvent("issue_comment", {
    action: "created",
    issue: issue(["stage:discovery"]),
    comment: {
      id: 78,
      body: "<!-- factory:analyst --> answer",
      user: copiedMarkerUser,
    },
  }, repo);
  assert.equal(humanCopiedMarker.action, "delete-comment");
});

test("AC1: requires mutually exclusive stages and rejects stage:review on issues", () => {
  const ambiguous = routeEvent("issues", {
    action: "labeled",
    sender: melkio,
    issue: issue(["stage:discovery", "stage:planning"]),
    label: { name: "stage:discovery", id: 6 },
  }, repo);
  assert.equal(ambiguous.action, "ignore");

  const reviewOnIssue = routeEvent("issues", {
    action: "labeled",
    sender: melkio,
    issue: issue(["stage:review"]),
    label: { name: "stage:review", id: 7 },
  }, repo);
  assert.equal(reviewOnIssue.action, "remove-label");

  const invalidPhase = routeEvent("issues", {
    action: "labeled",
    sender: melkio,
    issue: issue(["stage:unknown"]),
    label: { name: "stage:unknown", id: 9 },
  }, repo);
  assert.equal(invalidPhase.action, "remove-label");

  const nonPhaseLabel = routeEvent("issues", {
    action: "labeled",
    sender: melkio,
    issue: issue(["stage:discovery"]),
    label: { name: "type:feature", id: 10 },
  }, repo);
  assert.equal(nonPhaseLabel.action, "ignore");

  const botCreatedIssue = routeEvent("issues", {
    action: "opened",
    sender: factoryBot,
    issue: issue(["stage:planning"]),
  }, repo);
  assert.equal(botCreatedIssue.action, "ignore");
});

test("AC2: gives duplicate source events the same key and distinct comments distinct keys", () => {
  const event = {
    action: "created",
    issue: issue(["stage:discovery"]),
    comment: {
      id: 80,
      created_at: "2026-11-06T10:01:00Z",
      user: melkio,
      body: "Answer",
    },
  };
  const first = makeContract(routeEvent("issue_comment", event, repo), repo);
  const replay = makeContract(routeEvent("issue_comment", event, repo), repo);
  assert.equal(first.checkpoint.key, replay.checkpoint.key);

  const second = makeContract(routeEvent("issue_comment", {
    ...event,
    comment: { ...event.comment, id: 81 },
  }, repo), repo);
  assert.notEqual(first.checkpoint.key, second.checkpoint.key);
});

test("AC2: paginated comment cursors preserve arrivals during an active run", () => {
  const comments = [{ id: 21 }, { id: 23 }, { id: 22 }];
  assert.deepEqual(pendingInputs(comments, 20).map((comment) => comment.id), [21, 22, 23]);
  assert.deepEqual(pendingInputs(comments, 21).map((comment) => comment.id), [22, 23]);
  assert.deepEqual(pendingInputs(comments, 23), []);
  assert.equal(shouldSkipDispatch("waiting"), true);
  assert.equal(shouldSkipDispatch("completed"), true);
  assert.equal(shouldSkipDispatch("failed"), false);
  assert.equal(shouldSkipDispatch("blocked"), false);
  const submittedAfterBaseline = [{
    id: 21,
    state: "COMMENTED",
    submitted_at: "2026-11-06T10:05:00Z",
  }];
  assert.deepEqual(
    pendingInputs(submittedAfterBaseline, 21, "2026-11-06T10:04:00Z", "pull_request_review")
      .map((review) => review.id),
    [21],
  );
});

test("AC2: PR conversation commands are queued as follow-up inputs", () => {
  const pullRequest = pull([]);
  const comment = {
    id: 82,
    created_at: "2026-11-06T10:01:00Z",
    body: "/factory fix this",
    user: melkio,
  };
  const direct = routeEvent("issue_comment", {
    action: "created",
    issue: { ...pullRequest, pull_request: { url: "https://api.github.com/pulls/34" } },
    comment,
  }, repo);
  assert.equal(direct.action, "reconcile-resource");

  const queued = routeEvent("issue_comment", {
    action: "created",
    issue: { ...pullRequest, pull_request: { url: "https://api.github.com/pulls/34" } },
    pull_request: pullRequest,
    comment,
  }, repo);
  assert.equal(queued.step, "follow-up");
  assert.equal(queued.source.id, "82");
});

test("AC3: contract includes the explicit step, event revision, SHA, and waiting status", () => {
  const route = routeEvent("pull_request", {
    action: "synchronize",
    sender: copiedMarkerUser,
    pull_request: pull(["stage:review"]),
  }, repo);
  const contract = makeContract(route, repo);
  assert.equal(contract.step, "review");
  assert.equal(contract.event.id, "a".repeat(40));
  assert.equal(contract.expected.sha, "a".repeat(40));
  assert.equal(contract.checkpoint.status, "waiting");
  assert.equal(contract.result.status, "waiting");
  assert.equal(validateContract(contract, repo), contract);
});

test("AC3: bot-authored stage changes are never used as implicit chaining", () => {
  const route = routeEvent("pull_request", {
    action: "labeled",
    sender: factoryBot,
    pull_request: pull(["stage:review"]),
    label: { name: "stage:review", id: 8 },
  }, repo);
  assert.equal(route.action, "ignore");
});

test("AC4: rejects stale or malformed contracts and records only supported results", () => {
  const resource = pull(["stage:review"]);
  const contract = makeContract(routeEvent("pull_request", {
    action: "synchronize",
    sender: melkio,
    pull_request: resource,
  }, repo), repo);
  assert.equal(resourceMatchesContract(contract, resource, repo), true);
  assert.equal(resourceMatchesContract(contract, pull(["stage:review"], {
    head: { sha: "b".repeat(40), repo: { full_name: repo } },
  }), repo), false);
  assert.equal(resourceMatchesContract(contract, pull(["stage:ready"]), repo), false);
  assert.equal(resourceMatchesContract(contract, pull(["stage:review"], {
    body: "edited while running",
  }), repo), false);
  assert.equal(resourceMatchesContract(contract, pull(["stage:review"], {
    state: "closed",
  }), repo), false);

  const input = {
    id: 89,
    body: "/factory fix this",
    user: melkio,
  };
  const inputContract = makeContract(routeEvent("pull_request_review_comment", {
    action: "created",
    pull_request: resource,
    comment: input,
  }, repo), repo);
  assert.equal(inputMatchesContract(inputContract, input), true);
  assert.equal(inputMatchesContract(inputContract, { ...input, body: "/factory changed" }), false);
  assert.equal(inputMatchesContract(inputContract, { ...input, user: copiedMarkerUser }), false);
  assert.equal(inputMatchesContract(inputContract, null), false);

  assert.throws(() => validateContract({ ...contract, repository: "someone/else" }, repo));
  assert.throws(() => validateContract({
    ...contract,
    expected: { ...contract.expected, sha: "not-a-sha" },
  }, repo));
  assert.throws(() => validateContract({
    ...contract,
    result: { status: "success" },
  }, repo));
  assert.throws(() => validateContract({
    ...contract,
    checkpoint: { ...contract.checkpoint, status: "done" },
  }, repo));
});

test("AC5: parses one unquoted /factory command and ignores quoted or code examples", () => {
  assert.equal(parseFactoryCommand("> /factory do this"), null);
  assert.equal(parseFactoryCommand("`/factory do this`"), null);
  assert.equal(parseFactoryCommand("```text\n/factory do this\n```"), null);
  assert.equal(parseFactoryCommand("```text\n/factory do this"), null);
  assert.equal(parseFactoryCommand("    /factory do this"), null);
  assert.equal(parseFactoryCommand("Please fix this:\n/factory handle the null case"), "handle the null case");
  assert.equal(parseFactoryCommand("/factory first\n/factory second"), null);
  assert.equal(parseFactoryCommand("/factory"), null);
});

test("AC5: fork PRs and non-owner commands do not start a workflow", () => {
  const fork = routeEvent("pull_request", {
    action: "synchronize",
    sender: melkio,
    pull_request: pull(["stage:review"], {
      head: { sha: "b".repeat(40), repo: { full_name: "fork/CoursePilot" } },
    }),
  }, repo);
  assert.equal(fork.action, "ignore");

  const command = routeEvent("pull_request_review_comment", {
    action: "created",
    pull_request: pull(["stage:review"]),
    comment: {
      id: 88,
      created_at: "2026-11-06T10:02:00Z",
      body: "/factory run untrusted instruction",
      user: copiedMarkerUser,
    },
  }, repo);
  assert.equal(command.action, "delete-comment");
});

test("AC5: a command from melkio is carried by source ID, not interpolated into a shell command", () => {
  const route = routeEvent("issue_comment", {
    action: "created",
    issue: issue(["stage:discovery"]),
    comment: {
      id: 90,
      created_at: "2026-11-06T10:03:00Z",
      body: "/factory $(curl attacker)",
      user: melkio,
    },
  }, repo);
  assert.equal(route.step, "discovery");
  assert.equal(route.source.id, "90");
  assert.equal(Object.hasOwn(route, "command"), false);
});
