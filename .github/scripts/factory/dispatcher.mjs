import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

import {
  inputMatchesContract,
  isTrustedFactoryActor,
  makeContract,
  pendingInputs,
  resourceMatchesContract,
  routeEvent,
  routeResource,
  shouldSkipDispatch,
  STAGES,
  validateContract,
  WORKFLOWS,
} from "./core.mjs";

const STATE_MARKER = "<!-- factory-state:v1 -->";
const PAGE_SIZE = 100;
let trustedBot = null;
class GitHubApiError extends Error {
  constructor(method, path, status) {
    super(`GitHub API ${method} ${path} failed with HTTP ${status}.`);
    this.status = status;
  }
}

const repository = process.env.GITHUB_REPOSITORY;
const token = process.env.GITHUB_TOKEN;
const apiBase = process.env.GITHUB_API_URL ?? "https://api.github.com";
const summaryPath = process.env.GITHUB_STEP_SUMMARY;
const [owner, repo] = (repository ?? "").split("/");

if (!owner || !repo || !token) {
  throw new Error("Missing GitHub repository or token configuration.");
}

async function verifyStageLabels(resource) {
  const stageLabels = (resource.labels ?? [])
    .map((label) => label.name)
    .filter((name) => name.startsWith("stage:"));
  if (!stageLabels.length) return true;

  const timeline = await allPages(`${issuePath(resource.number)}/timeline`);
  const transitions = new Map();
  for (const event of timeline.sort((left, right) =>
    Date.parse(left.created_at) - Date.parse(right.created_at) ||
    (left.id ?? 0) - (right.id ?? 0))) {
    if (!["labeled", "unlabeled"].includes(event.event) ||
      !event.label?.name?.startsWith("stage:")) continue;
    transitions.set(event.label.name, event);
  }

  for (const label of stageLabels) {
    const transition = transitions.get(label);
    let actor = transition?.actor ?? transition?.user;
    if (actor?.login === "github-actions[bot]" && actor.type !== "Bot") {
      if (!trustedBot) {
        const verified = await api("/users/github-actions%5Bbot%5D");
        if (verified?.login === "github-actions[bot]" && verified?.type === "Bot") {
          trustedBot = verified;
        }
      }
      actor = trustedBot;
    }
    const validResourceLabel = STAGES.includes(label) &&
      (resource.head || !label.startsWith("stage:review"));
    const authorized = validResourceLabel &&
      (actor?.login === "melkio" && actor.type !== "Bot" || isTrustedFactoryActor(actor));
    if (!authorized) {
      await cleanup({ action: "remove-label", resourceNumber: resource.number, label });
      await appendSummary(`- Removed unverified phase label from #${resource.number}.`);
      return false;
    }
  }
  if (stageLabels.length > 1) {
    const latest = stageLabels
      .map((label) => transitions.get(label))
      .filter(Boolean)
      .sort((left, right) =>
        Date.parse(right.created_at) - Date.parse(left.created_at) ||
        (right.id ?? 0) - (left.id ?? 0))[0];
    const retained = latest?.label?.name;
    for (const label of stageLabels) {
      if (label !== retained) {
        await cleanup({ action: "remove-label", resourceNumber: resource.number, label });
      }
    }
    await appendSummary(`- Normalized mutually exclusive phase labels on #${resource.number}.`);
    return false;
  }
  return true;
}

async function request(path, method = "GET", body) {
  const url = `${apiBase.replace(/\/+$/, "")}/${path.replace(/^\/+/, "")}`;
  for (let attempt = 0; attempt < 2; attempt += 1) {
    let response;
    try {
      response = await fetch(url, {
        method,
        headers: {
          accept: "application/vnd.github+json",
          authorization: "Bearer " + token,
          "x-github-api-version": "2022-11-28",
          ...(body === undefined ? {} : { "content-type": "application/json" }),
        },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
    } catch {
      if (attempt === 0) {
        await new Promise((resolve) => setTimeout(resolve, 1_000));
        continue;
      }
      throw new Error(`GitHub API ${method} ${path} request failed after one retry.`);
    }
    if (response.ok) {
      if (response.status === 204) return null;
      return { data: await response.json(), headers: response.headers };
    }
    if (attempt === 0 && [429, 500, 502, 503, 504].includes(response.status)) {
      const retryAfter = Number(response.headers.get("retry-after"));
      const delay = Number.isFinite(retryAfter) && retryAfter > 0
        ? Math.min(retryAfter * 1_000, 5_000)
        : 1_000;
      await response.body?.cancel();
      await new Promise((resolve) => setTimeout(resolve, delay));
      continue;
    }
    throw new GitHubApiError(method, path, response.status);
  }
  throw new Error(`GitHub API ${method} ${path} request failed.`);
}

async function api(path, method = "GET", body) {
  const result = await request(path, method, body);
  return result?.data ?? null;
}

async function allPages(path) {
  const results = [];
  for (let page = 1; page <= 100; page += 1) {
    const separator = path.includes("?") ? "&" : "?";
    const response = await request(`${path}${separator}per_page=${PAGE_SIZE}&page=${page}`);
    results.push(...(Array.isArray(response.data) ? response.data : response.data.workflows ?? []));
    if (!response.headers.get("link")?.includes('rel="next"')) return results;
  }
  throw new Error(`GitHub API pagination limit reached for ${path}.`);
}

function issuePath(number) {
  return `/repos/${owner}/${repo}/issues/${number}`;
}

function pullPath(number) {
  return `/repos/${owner}/${repo}/pulls/${number}`;
}

function maxId(items) {
  return items.reduce((maximum, item) =>
    Number.isSafeInteger(item.id) ? Math.max(maximum, item.id) : maximum, 0);
}

async function loadResource(number, preferredType) {
  const issue = await api(issuePath(number));
  if (preferredType === "pull_request" || issue.pull_request) {
    return await api(pullPath(number));
  }
  return issue;
}

function newState(resource) {
  return {
    schema_version: 1,
    resource: {
      type: resource.head ? "pull_request" : "issue",
      number: resource.number,
    },
    cursors: {
      issue_comment: 0,
      pull_request_review_comment: 0,
      pull_request_review: 0,
    },
    current_stage: null,
    active_key: null,
    initialized: false,
    baseline_at: null,
    entries: [],
  };
}

function parseState(body, resource) {
  if (!body?.startsWith(`${STATE_MARKER}\n`)) return null;
  try {
    const state = JSON.parse(body.slice(STATE_MARKER.length).trim());
    if (state.schema_version !== 1 ||
      state.resource?.number !== resource.number ||
      state.resource?.type !== (resource.head ? "pull_request" : "issue") ||
      !Array.isArray(state.entries) ||
      !state.cursors ||
      !Number.isInteger(state.cursors.issue_comment)) return null;
    return state;
  } catch {
    return null;
  }
}

async function loadState(resource) {
  const comments = await allPages(`${issuePath(resource.number)}/comments?sort=created&direction=asc`);
  const stateComment = comments
    .filter((comment) =>
      comment.user?.login === "github-actions[bot]" &&
      comment.user?.type === "Bot" &&
      comment.body?.startsWith(`${STATE_MARKER}\n`))
    .map((comment) => ({ comment, state: parseState(comment.body, resource) }))
    .filter((entry) => entry.state)
    .at(-1);
  return {
    state: stateComment?.state ?? newState(resource),
    commentId: stateComment?.comment.id ?? null,
  };
}

async function saveState(resource, state, commentId) {
  const body = `${STATE_MARKER}\n${JSON.stringify(state)}`;
  if (Buffer.byteLength(body, "utf8") > 60_000) {
    throw new Error("Factory checkpoint exceeded the GitHub comment size limit.");
  }
  if (commentId) {
    await api(`/repos/${owner}/${repo}/issues/comments/${commentId}`, "PATCH", { body });
    return commentId;
  }
  const comment = await api(`${issuePath(resource.number)}/comments`, "POST", { body });
  return comment.id;
}

function updateEntry(state, contract, status, runId = null, message = null) {
  const entry = {
    key: contract.checkpoint.key,
    step: contract.step,
    status,
    workflow_run_id: runId,
    updated_at: new Date().toISOString(),
    message,
    contract: structuredClone(contract),
  };
  const existing = state.entries.findIndex((item) => item.key === entry.key);
  if (existing >= 0) state.entries[existing] = entry;
  else state.entries.push(entry);
  if (status === "completed") delete entry.contract;
  state.entries = state.entries.slice(-50);
  state.active_key = status === "waiting" ? entry.key : null;
  contract.checkpoint.status = status;
  contract.result.status = status;
  return entry;
}

async function appendSummary(text) {
  if (!summaryPath) return;
  const { appendFile } = await import("node:fs/promises");
  await appendFile(summaryPath, `${text}\n`, "utf8");
}

function sha256(value) {
  return createHash("sha256").update(value ?? "").digest("hex");
}

async function isEligible(contract, resource) {
  if (!await verifyStageLabels(resource) ||
    !resourceMatchesContract(contract, resource, repository)) return false;

  if (["issue_comment", "review_comment", "review"].includes(contract.event?.type)) {
    const sourcePath = contract.event.type === "issue_comment"
      ? `/repos/${owner}/${repo}/issues/comments/${contract.event.id}`
      : contract.event.type === "review_comment"
        ? `/repos/${owner}/${repo}/pulls/comments/${contract.event.id}`
        : `${pullPath(contract.resource.number)}/reviews/${contract.event.id}`;
    let source;
    try {
      source = await api(sourcePath);
    } catch (error) {
      if (error instanceof GitHubApiError && error.status === 404) return false;
      throw error;
    }
    if (!inputMatchesContract(contract, source)) return false;
  }
  return true;
}

async function findWorkflow(file) {
  const workflows = await allPages(`/repos/${owner}/${repo}/actions/workflows`);
  return workflows.find((workflow) =>
    workflow.path === `.github/workflows/${file}` && workflow.state === "active");
}

async function dispatchContract(route, resource, state, commentId, defaultBranch) {
  const contract = makeContract(route, repository);
  validateContract(contract, repository);
  const existing = state.entries.find((entry) => entry.key === contract.checkpoint.key);
  if (existing && shouldSkipDispatch(existing.status)) {
    return { status: existing.status, contract, commentId };
  }

  if (!await isEligible(contract, resource)) {
    updateEntry(state, contract, "blocked", null, "State or SHA changed before dispatch.");
    commentId = await saveState(resource, state, commentId);
    await appendSummary(`- **Blocked** \`${route.step}\` #${resource.number}: state or SHA changed.`);
    return { status: "blocked", contract, commentId };
  }

  const workflow = await findWorkflow(route.workflow);
  if (!workflow) {
    updateEntry(state, contract, "blocked", null, "Step workflow is not installed yet.");
    commentId = await saveState(resource, state, commentId);
    await appendSummary(`- **Waiting** \`${route.step}\` #${resource.number}: \`${route.workflow}\` is not installed yet.`);
    return { status: "blocked", contract, commentId };
  }

  if (Object.hasOwn(state.cursors, route.source.type)) {
    const sourceId = Number(route.source.id);
    if (Number.isSafeInteger(sourceId)) state.cursors[route.source.type] = sourceId;
  }
  updateEntry(state, contract, "waiting", null, "Dispatch requested.");
  commentId = await saveState(resource, state, commentId);
  try {
    await api(`/repos/${owner}/${repo}/actions/workflows/${workflow.id}/dispatches`, "POST", {
      ref: defaultBranch,
      inputs: { contract: JSON.stringify(contract) },
    });
  } catch (error) {
    updateEntry(state, contract, "failed", null, "Workflow dispatch failed.");
    await saveState(resource, state, commentId);
    throw error;
  }
  await appendSummary(`- **Dispatched** \`${route.step}\` #${resource.number}; input \`${contract.event.id}\`, SHA \`${contract.expected.sha ?? "n/a"}\`.`);
  return { status: "waiting", contract, commentId };
}

async function cleanup(route) {
  const number = route.resourceNumber;
  if (!Number.isInteger(number) || number < 1) return;
  if (route.action === "delete-comment" && Number.isInteger(route.commentId)) {
    const path = route.commentType === "review"
      ? `/repos/${owner}/${repo}/pulls/comments/${route.commentId}`
      : `/repos/${owner}/${repo}/issues/comments/${route.commentId}`;
    await api(path, "DELETE");
    await appendSummary(`- Removed unauthorized comment \`${route.commentId}\` from #${number}.`);
  } else if (route.action === "remove-label" && route.label) {
    await api(`${issuePath(number)}/labels/${encodeURIComponent(route.label)}`, "DELETE");
    await appendSummary(`- Removed unauthorized label \`${route.label}\` from #${number}.`);
  } else if (route.action === "dismiss-review" && Number.isInteger(route.reviewId)) {
    await api(`/repos/${owner}/${repo}/pulls/${number}/reviews/${route.reviewId}/dismissals`, "PUT", {
      message: "Only @melkio may interact with the factory during its test phase.",
    });
    await appendSummary(`- Dismissed unauthorized review \`${route.reviewId}\` on #${number}.`);
  }
}

function inputSources(resource) {
  return resource.head
    ? ["issue_comment", "pull_request_review_comment", "pull_request_review"]
    : ["issue_comment"];
}

async function loadInputs(resource, sourceType) {
  const path = sourceType === "issue_comment"
    ? `${issuePath(resource.number)}/comments?sort=created&direction=asc`
    : sourceType === "pull_request_review_comment"
      ? `${pullPath(resource.number)}/comments?sort=created&direction=asc`
      : `${pullPath(resource.number)}/reviews`;
  return await allPages(path);
}

function routeInput(sourceType, input, resource) {
  if (sourceType === "issue_comment") {
    const issue = resource.head
      ? { ...resource, pull_request: { url: `${pullPath(resource.number)}` } }
      : resource;
    return routeEvent("issue_comment", {
      action: "created",
      issue,
      pull_request: resource.head ? resource : undefined,
      comment: input,
    }, repository);
  }
  if (sourceType === "pull_request_review_comment") {
    return routeEvent("pull_request_review_comment", {
      action: "created",
      pull_request: resource,
      comment: input,
    }, repository);
  }
  return routeEvent("pull_request_review", {
    action: input.state === "PENDING" ? "pending" : "submitted",
    pull_request: resource,
    review: input,
  }, repository);
}

async function waitForActive(state, resource, commentId) {
  const active = state.entries.find((entry) => entry.key === state.active_key);
  if (!active || active.status !== "waiting") return { state, commentId, waiting: false };
  if (active.workflow_run_id) {
    const run = await api(`/repos/${owner}/${repo}/actions/runs/${active.workflow_run_id}`);
    if (run.status !== "completed") return { state, commentId, waiting: true };
    if (run.conclusion === "success") {
      updateEntry(state, active.contract, "completed", active.workflow_run_id);
    } else {
      updateEntry(state, active.contract, "failed", active.workflow_run_id, "Workflow run did not succeed.");
    }
    commentId = await saveState(resource, state, commentId);
    return { state, commentId, waiting: false };
  }
  const age = Date.now() - Date.parse(active.updated_at);
  if (Number.isFinite(age) && age > 15 * 60 * 1000) {
    updateEntry(state, active.contract, "failed", null,
      "Dispatched workflow did not register a run.");
    commentId = await saveState(resource, state, commentId);
    return { state, commentId, waiting: false };
  }
  return { state, commentId, waiting: true };
}

async function processInputs(resource, state, commentId, defaultBranch) {
  for (const sourceType of inputSources(resource)) {
    const inputs = await loadInputs(resource, sourceType);
    const cursor = state.cursors[sourceType] ?? 0;
    for (const input of pendingInputs(inputs, cursor, state.baseline_at, sourceType)) {
      if (sourceType === "pull_request_review" && input.state === "PENDING") {
        if (Date.parse(input.created_at ?? "") < Date.parse(state.baseline_at ?? "")) {
          if (input.id > (state.cursors[sourceType] ?? 0)) {
            state.cursors[sourceType] = input.id;
            commentId = await saveState(resource, state, commentId);
          }
          continue;
        }
        if (input.user?.login !== "melkio" || input.user?.type === "Bot") {
          state.cursors[sourceType] = input.id;
          commentId = await saveState(resource, state, commentId);
          continue;
        }
        return { state, commentId, dispatched: false, stop: true };
      }
      const route = routeInput(sourceType, input, resource);
      if (route.action === "delete-comment" || route.action === "dismiss-review") {
        await cleanup(route);
        state.cursors[sourceType] = input.id;
        commentId = await saveState(resource, state, commentId);
        continue;
      }
      if (route.action === "dispatch") {
        const result = await dispatchContract(route, resource, state, commentId, defaultBranch);
        if (result.status === "waiting" || result.status === "blocked") {
          if (result.status === "waiting") state.cursors[sourceType] = input.id;
          return {
            state,
            commentId: result.commentId,
            dispatched: result.status === "waiting",
            stop: true,
          };
        }
      }
      state.cursors[sourceType] = input.id;
      commentId = await saveState(resource, state, commentId);
    }
  }
  return { state, commentId, dispatched: false, stop: false };
}

async function initializeCursors(resource, state, sourceInput, ignoreExisting = false) {
  const stage = (resource.labels ?? [])
    .map((label) => label.name)
    .find((label) => label.startsWith("stage:"));
  let stageStartedAt = null;
  if (stage && !ignoreExisting) {
    const timeline = await allPages(`${issuePath(resource.number)}/timeline`);
    const transition = timeline
      .filter((event) =>
        ["labeled", "unlabeled"].includes(event.event) &&
        event.label?.name === stage)
      .sort((left, right) =>
        Date.parse(left.created_at) - Date.parse(right.created_at) ||
        (left.id ?? 0) - (right.id ?? 0))
      .at(-1);
    if (transition?.event === "labeled") {
      const timestamp = Date.parse(transition.created_at);
      if (Number.isFinite(timestamp)) stageStartedAt = timestamp;
    }
  }

  state.baseline_at = stageStartedAt !== null
    ? new Date(stageStartedAt).toISOString()
    : new Date().toISOString();
  for (const sourceType of inputSources(resource)) {
    const inputs = await loadInputs(resource, sourceType);
    if (ignoreExisting) {
      state.cursors[sourceType] = maxId(inputs);
    } else if (stageStartedAt !== null) {
      state.cursors[sourceType] = inputs
        .filter((input) => {
          const createdAt = sourceType === "pull_request_review"
            ? input.submitted_at ? Date.parse(input.submitted_at) : Number.POSITIVE_INFINITY
            : Date.parse(input.created_at);
          return createdAt < stageStartedAt;
        })
        .reduce((maximum, input) => Number.isSafeInteger(input.id)
          ? Math.max(maximum, input.id)
          : maximum, 0);
    } else if (sourceInput?.type === sourceType) {
      const observedId = Number(sourceInput.id);
      state.cursors[sourceType] = Number.isSafeInteger(observedId)
        ? Math.max(0, observedId - 1)
        : maxId(inputs);
      const observedInput = inputs.find((input) => input.id === observedId);
      const observedAt = sourceType === "pull_request_review"
        ? observedInput?.submitted_at
        : observedInput?.created_at;
      if (observedAt) state.baseline_at = observedAt;
    } else {
      state.cursors[sourceType] = maxId(inputs);
    }
  }
  state.initialized = true;
}

async function reconcileResource(number, preferredType, defaultBranch, sourceMode = "reconcile", sourceInput = null) {
  const resource = await loadResource(number, preferredType);
  if (!resource || resource.state !== "open") return;
  if (!await verifyStageLabels(resource)) return;
  const { state: initialState, commentId: initialCommentId } = await loadState(resource);
  let state = initialState;
  let commentId = initialCommentId;
  if (!state.initialized) {
    await initializeCursors(resource, state, sourceInput);
    commentId = await saveState(resource, state, commentId);
  }

  const active = await waitForActive(state, resource, commentId);
  state = active.state;
  commentId = active.commentId;
  if (active.waiting) return;

  const failed = [...state.entries].reverse()
    .find((entry) => ["failed", "blocked"].includes(entry.status) && entry.contract);
  if (failed) {
    const previousContract = failed.contract;
    const route = {
      action: "dispatch",
      step: previousContract.step,
      workflow: WORKFLOWS[previousContract.step],
      resource: previousContract.resource,
      source: previousContract.event,
      expected: previousContract.expected,
    };
    const retry = await dispatchContract(route, resource, state, commentId, defaultBranch);
    if (retry.status === "waiting") return;
  }

  const inputResult = await processInputs(resource, state, commentId, defaultBranch);
  state = inputResult.state;
  commentId = inputResult.commentId;
  if (inputResult.stop) return;

  const route = routeResource(
    resource,
    repository,
    sourceMode,
    sourceMode === "event" ? process.env.GITHUB_RUN_ID : `stage:${resource.number}`,
    sha256(resource.body),
  );
  if (route.action !== "dispatch") {
    state.current_stage = null;
    await saveState(resource, state, commentId);
    return;
  }

  if (route.step !== "unblock" && state.current_stage === route.stage) {
    const previous = state.entries.find((entry) => entry.step === route.step);
    if (previous && shouldSkipDispatch(previous.status)) return;
  }
  state.current_stage = route.stage;
  if (route.step === "unblock") {
    route.source.id = `${process.env.GITHUB_RUN_ID}`;
  }
  await dispatchContract(route, resource, state, commentId, defaultBranch);
}

async function dispatchResourceRun(number, defaultBranch) {
  const workflow = await findWorkflow("factory-dispatcher.yml");
  if (!workflow) throw new Error("The dispatcher workflow is missing from the default branch.");
  await api(`/repos/${owner}/${repo}/actions/workflows/${workflow.id}/dispatches`, "POST", {
    ref: defaultBranch,
    inputs: { internal: "true", resource_number: String(number) },
  });
}

async function scheduleReconciliation(defaultBranch) {
  const open = await allPages(`/repos/${owner}/${repo}/issues?state=open&sort=updated&direction=asc`);
  const candidates = open.filter((resource) =>
    resource.labels?.some((label) => label.name.startsWith("stage:")) || resource.pull_request);
  await appendSummary(`- Reconciliation found ${candidates.length} active resource(s); each is dispatched on its own resource concurrency key.`);
  for (let offset = 0; offset < candidates.length; offset += 10) {
    await Promise.all(candidates.slice(offset, offset + 10)
      .map((resource) => dispatchResourceRun(resource.number, defaultBranch)));
  }
}

async function initializeResources() {
  const open = await allPages(`/repos/${owner}/${repo}/issues?state=open&sort=updated&direction=asc`);
  const candidates = open.filter((resource) =>
    resource.labels?.some((label) => label.name.startsWith("stage:")) || resource.pull_request);
  for (let offset = 0; offset < candidates.length; offset += 10) {
    await Promise.all(candidates.slice(offset, offset + 10).map(async (candidate) => {
      const resource = await loadResource(candidate.number, candidate.pull_request ? "pull_request" : "issue");
      if (!await verifyStageLabels(resource)) return;
      const { state, commentId } = await loadState(resource);
      if (state.initialized) return;
      await initializeCursors(resource, state, null, true);
      state.current_stage = (resource.labels ?? [])
        .map((label) => label.name)
        .find((label) => label.startsWith("stage:")) ?? null;
      await saveState(resource, state, commentId);
    }));
  }
  await appendSummary(`- Initialized checkpoints for ${candidates.length} active resource(s); no step was dispatched.`);
}

async function currentDefaultBranch() {
  const current = await api(`/repos/${owner}/${repo}`);
  if (!current.default_branch || current.default_branch.includes("..")) {
    throw new Error("Could not resolve the repository default branch.");
  }
  return current.default_branch;
}

async function readJson(path) {
  return JSON.parse(await readFile(path, "utf8"));
}

async function setActionOutput(name, value) {
  const outputPath = process.env.GITHUB_OUTPUT;
  if (!outputPath) return;
  const { appendFile } = await import("node:fs/promises");
  await appendFile(outputPath, `${name}=${value}\n`, "utf8");
}

export async function executeRuntime(operation, contractValue) {
  const contract = validateContract(contractValue, repository);
  const resource = await loadResource(contract.resource.number, contract.resource.type);
  const { state, commentId } = await loadState(resource);
  const entry = state.entries.find((item) => item.key === contract.checkpoint.key);
  if (operation === "verify") {
    if (entry?.status !== "waiting" || entry.workflow_run_id !== process.env.GITHUB_RUN_ID) {
      throw new Error("The current workflow does not own an active checkpoint to publish.");
    }
    if (!await isEligible(contract, resource)) throw new Error("Factory state, input, or expected SHA changed during execution.");
    return;
  }
  if (operation === "start") {
    if (entry?.status === "completed" ||
      (entry?.status === "waiting" && entry.workflow_run_id &&
        entry.workflow_run_id !== process.env.GITHUB_RUN_ID)) {
      await setActionOutput("should_skip", "true");
      return;
    }
    if (!await isEligible(contract, resource)) {
      updateEntry(state, contract, "blocked", process.env.GITHUB_RUN_ID, "Input state changed before start.");
      await saveState(resource, state, commentId);
      throw new Error("Factory state or expected SHA changed before start.");
    }
    updateEntry(state, contract, "waiting", process.env.GITHUB_RUN_ID);
    await saveState(resource, state, commentId);
    await setActionOutput("should_skip", "false");
    return;
  }
  if (operation === "result") {
    const result = process.env.FACTORY_RESULT;
    if (!["completed", "blocked", "failed"].includes(result)) {
      throw new Error("FACTORY_RESULT must be completed, blocked, or failed.");
    }
    if (!entry || entry.workflow_run_id !== process.env.GITHUB_RUN_ID) {
      throw new Error("Cannot checkpoint a run that was not started by this workflow.");
    }
    if (entry.status === result) return;
    if (entry.status !== "waiting") throw new Error("Only an active run can change its checkpoint result.");
    updateEntry(state, contract, result, process.env.GITHUB_RUN_ID);
    await saveState(resource, state, commentId);
    return;
  }
  if (operation === "next") {
    if (!entry || entry.status !== "completed" ||
      entry.workflow_run_id !== process.env.GITHUB_RUN_ID) {
      throw new Error("The current step must be checkpointed as completed before chaining.");
    }
    const defaultBranch = await currentDefaultBranch();
    await reconcileResource(resource.number, contract.resource.type, defaultBranch, "next");
    return;
  }
  throw new Error(`Unsupported runtime operation: ${operation}`);
}

async function main() {
  const defaultBranch = await currentDefaultBranch();
  const eventName = process.env.GITHUB_EVENT_NAME;
  const event = await readJson(process.env.GITHUB_EVENT_PATH);
  const route = routeEvent(eventName, event, repository);

  await appendSummary(`## Factory dispatcher\n\n- Run: \`${process.env.GITHUB_RUN_ID}\`\n- Event: \`${eventName}\`\n- Default branch: \`${defaultBranch}\``);

  if (route.action === "delete-comment" || route.action === "remove-label" || route.action === "dismiss-review") {
    await cleanup(route);
    return;
  }
  if (route.action === "ignore") {
    await appendSummary("- Ignored: event is not actionable in the current state.");
    return;
  }
  if (route.action === "initialize") {
    await initializeResources();
    return;
  }
  if (route.action === "reconcile") {
    if (eventName === "schedule") {
      await scheduleReconciliation(defaultBranch);
      return;
    }
    const number = Number(event.inputs?.resource_number);
    if (Number.isSafeInteger(number) && number > 0) {
      const resource = await loadResource(number);
      await reconcileResource(number, resource.head ? "pull_request" : "issue", defaultBranch, "manual");
    } else {
      await scheduleReconciliation(defaultBranch);
    }
    return;
  }
  if (route.action === "reconcile-resource") {
    const number = Number(route.resourceNumber ?? event.inputs?.resource_number);
    if (!Number.isSafeInteger(number) || number < 1) throw new Error("Invalid internal resource number.");
    const resource = await loadResource(number, route.resourceType);
    await reconcileResource(
      number,
      route.resourceType ?? (resource.head ? "pull_request" : "issue"),
      defaultBranch,
      "event",
      ["issue_comment", "pull_request_review_comment", "pull_request_review"].includes(eventName)
        ? {
          type: eventName === "pull_request_review_comment"
            ? "pull_request_review_comment"
            : eventName === "pull_request_review"
              ? "pull_request_review"
              : "issue_comment",
          id: event.comment?.id ?? event.review?.id,
        }
        : null,
    );
    return;
  }
  if (route.action === "dispatch") {
    if (["issue_comment", "pull_request_review_comment", "pull_request_review"].includes(eventName)) {
      const sourceType = eventName === "pull_request_review_comment"
        ? "pull_request_review_comment"
        : eventName === "pull_request_review"
          ? "pull_request_review"
          : "issue_comment";
      await reconcileResource(route.resource.number, route.resource.type, defaultBranch, "event", {
        type: sourceType,
        id: event.comment?.id ?? event.review?.id,
      });
      return;
    }
    const resource = await loadResource(route.resource.number, route.resource.type);
    const currentRoute = routeResource(
      resource,
      repository,
      route.source.type,
      route.source.id,
      route.source.revision,
    );
    if (currentRoute.action !== "dispatch" ||
      currentRoute.step !== route.step ||
      currentRoute.expected.sha !== route.expected.sha ||
      currentRoute.expected.state_revision !== route.expected.state_revision) {
      await appendSummary("- Ignored stale event: resource stage, body, or SHA changed before dispatch.");
      return;
    }
    const { state, commentId } = await loadState(resource);
    state.current_stage = currentRoute.stage;
    const savedCommentId = await saveState(resource, state, commentId);
    await dispatchContract(currentRoute, resource, state, savedCommentId, defaultBranch);
  }
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href) {
  try {
    const [operation, contractPath] = process.argv.slice(2);
    if (operation) {
      if (!contractPath) throw new Error("A contract file is required.");
      await executeRuntime(operation, await readJson(contractPath));
    } else {
      await main();
    }
  } catch (error) {
    await appendSummary(`- **Failed**: ${error.message}`);
    console.error(error.message);
    process.exitCode = 1;
  }
}
