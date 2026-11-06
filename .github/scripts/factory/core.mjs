import { createHash } from "node:crypto";

export const CLI_VERSION = "v1.0.106";
export const MODELS = Object.freeze({
  "GPT-6.1 Sol": "gpt-6.1-sol",
  "GPT-6.1 Terra": "gpt-6.1-terra",
  "Claude Opus 5.5": "claude-opus-5.5",
});

const STEP_STAGES = Object.freeze({
  discovery: "stage:discovery",
  "functional-consolidation": "stage:discovery-approved",
  planning: "stage:planning",
  unblock: "stage:blocked",
  design: "stage:design",
  "technical-consolidation": "stage:design-approved",
  "design-verification": "stage:ready",
  review: "stage:review",
  lessons: "stage:review-completed",
});

export const WORKFLOWS = Object.freeze({
  discovery: "factory-discovery.yml",
  "functional-consolidation": "factory-functional-consolidation.yml",
  planning: "factory-planning.yml",
  unblock: "factory-unblock.yml",
  design: "factory-design.yml",
  "technical-consolidation": "factory-technical-consolidation.yml",
  "design-verification": "factory-design-verification.yml",
  implementation: "factory-implementation.yml",
  review: "factory-review.yml",
  "follow-up": "factory-follow-up.yml",
  lessons: "factory-lessons.yml",
});

export const STAGES = Object.freeze([
  "stage:discovery",
  "stage:discovery-approved",
  "stage:planning",
  "stage:blocked",
  "stage:design",
  "stage:design-approved",
  "stage:ready",
  "stage:review",
  "stage:review-completed",
]);

export function pendingInputs(items, cursor, baselineAt = null, sourceType = null) {
  const baseline = Date.parse(baselineAt ?? "");
  return items
    .filter((item) => Number.isSafeInteger(item.id) &&
      (item.id > cursor ||
        (sourceType === "pull_request_review" &&
          Number.isFinite(baseline) &&
          Date.parse(item.submitted_at ?? "") >= baseline)))
    .sort((left, right) => left.id - right.id);
}

export function shouldSkipDispatch(status) {
  return status === "waiting" || status === "completed";
}

export function resourceMatchesContract(contract, resource, repository) {
  if (!resource || resource.number !== contract.resource.number ||
    resource.state !== "open" ||
    createHash("sha256").update(resource.body ?? "").digest("hex") !== contract.expected.state_revision) {
    return false;
  }
  const pullRequest = isPullRequest(resource);
  if ((contract.resource.type === "pull_request") !== pullRequest) return false;
  const labels = resourceLabels(resource);
  const stages = labels.filter((label) => label.startsWith("stage:"));
  if (stages.length > 1 || stages.some((stage) => !STAGES.includes(stage))) return false;

  if (pullRequest) {
    if (resource.head?.repo?.full_name !== repository ||
      resource.head?.sha !== contract.expected.sha) return false;
    if (contract.step === "follow-up") {
      return stages.length === 0 || stages[0] === "stage:review";
    }
    return ["review", "lessons"].includes(contract.step) &&
      stages.length === 1 && stages[0] === STEP_STAGES[contract.step];
  }
  return contract.expected.sha === null &&
    !["review", "lessons", "follow-up"].includes(contract.step) &&
    stages.length === 1 &&
    stages[0] === STEP_STAGES[contract.step] &&
    (contract.step !== "design" || labels.includes("type:feature"));
}

export function inputMatchesContract(contract, input) {
  if (!input || input.user?.login !== "melkio" || input.user?.type === "Bot" ||
    contentRevision(input.body) !== contract.event.revision) return false;
  return contract.event.type !== "review" || input.state !== "PENDING";
}

const ISSUE_STEPS = Object.freeze({
  "stage:discovery": "discovery",
  "stage:discovery-approved": "functional-consolidation",
  "stage:planning": "planning",
  "stage:blocked": "unblock",
  "stage:design-approved": "technical-consolidation",
  "stage:ready": "design-verification",
});

const PR_STEPS = Object.freeze({
  "stage:review": "review",
  "stage:review-completed": "lessons",
});

export function isAuthorizedActor(user) {
  return user?.login === "melkio" && user?.type !== "Bot";
}

export function isTrustedFactoryActor(user) {
  return user?.login === "github-actions[bot]" && user?.type === "Bot";
}

function isPullRequest(resource) {
  return resource?.pull_request !== undefined || resource?.head !== undefined;
}

function issueStep(resource, labels) {
  const stages = labels.filter((label) => label.startsWith("stage:"));
  if (stages.length !== 1) return { error: "Expected exactly one stage label." };

  const stage = stages[0];
  if (!STAGES.includes(stage)) return { error: `Unknown stage label: ${stage}.` };
  if (stage.startsWith("stage:review")) {
    return { error: "Review stages are only valid on pull requests." };
  }
  if (stage === "stage:design" && !labels.includes("type:feature")) {
    return { error: "Only type:feature issues can enter design." };
  }

  const step = stage === "stage:design" ? "design" : ISSUE_STEPS[stage];
  return step
    ? { step, stage }
    : { error: `No issue workflow is defined for ${stage}.` };
}

function pullRequestStep(resource, labels) {
  const stages = labels.filter((label) => label.startsWith("stage:"));
  if (stages.length !== 1) return { error: "Expected exactly one stage label." };

  const stage = stages[0];
  if (!STAGES.includes(stage)) return { error: `Unknown stage label: ${stage}.` };
  const step = PR_STEPS[stage];
  return step
    ? { step, stage }
    : { error: `No pull request workflow is defined for ${stage}.` };
}

function resourceLabels(resource) {
  return Array.isArray(resource?.labels)
    ? resource.labels.map((label) => typeof label === "string" ? label : label.name)
    : [];
}

function contentRevision(value) {
  return createHash("sha256").update(value ?? "").digest("hex");
}

function isOpen(resource) {
  return resource?.state === "open" || resource?.state === undefined;
}

function isTrustedPullRequest(resource, repository) {
  return resource?.head?.repo?.full_name === repository;
}

function deleteCommentPlan(eventName, payload, comment) {
  if (isTrustedFactoryActor(comment?.user)) return { action: "ignore" };
  return {
    action: "delete-comment",
    commentType: eventName === "pull_request_review_comment" ? "review" : "issue",
    commentId: comment?.id,
    resourceNumber: payload.pull_request?.number ?? payload.issue?.number,
  };
}

export function parseFactoryCommand(body) {
  if (typeof body !== "string") return null;

  let fence = null;
  const plainText = body.split("\n").filter((line) => {
    const marker = /^\s{0,3}(`{3,}|~{3,})/.exec(line)?.[1];
    if (fence) {
      if (marker && marker[0] === fence.character && marker.length >= fence.length) {
        fence = null;
      }
      return false;
    }
    if (marker) {
      fence = { character: marker[0], length: marker.length };
      return false;
    }
    return !/^\s*>/.test(line) && !/^ {4}/.test(line);
  }).map((line) => line.replace(/(`+).*?\1/g, "")).join("\n");
  const matches = [...plainText.matchAll(/^[ \t]*\/factory(?:[ \t]+([^\n]*))?[ \t]*$/gm)];
  if (matches.length !== 1) return null;

  const instructions = matches[0][1]?.trim();
  return instructions ? instructions : null;
}

function makeRoute(step, resource, sourceType, sourceId, sourceRevision) {
  const number = resource.number;
  const pullRequest = isPullRequest(resource);
  return {
    action: "dispatch",
    step,
    stage: resourceLabels(resource).find((label) => STAGES.includes(label)) ?? null,
    workflow: WORKFLOWS[step],
    resource: {
      type: pullRequest ? "pull_request" : "issue",
      number,
    },
    source: {
      type: sourceType,
      id: String(sourceId),
      revision: String(sourceRevision ?? ""),
    },
    expected: {
      sha: pullRequest ? resource.head?.sha ?? null : null,
      state_revision: contentRevision(resource.body),
    },
  };
}

function routeCurrentState(resource, repository, sourceType, sourceId, sourceRevision) {
  if (!resource || !Number.isInteger(resource.number) || !isOpen(resource)) {
    return { action: "ignore", reason: "Resource is missing or closed." };
  }

  const labels = resourceLabels(resource);
  const candidate = isPullRequest(resource)
    ? pullRequestStep(resource, labels)
    : issueStep(resource, labels);
  if (candidate.error) return { action: "ignore", reason: candidate.error };

  if (isPullRequest(resource) && !isTrustedPullRequest(resource, repository)) {
    return { action: "ignore", reason: "Pull request head is not in this repository." };
  }

  return makeRoute(candidate.step, resource, sourceType, sourceId, sourceRevision);
}

export function routeResource(resource, repository, sourceType, sourceId, sourceRevision) {
  return routeCurrentState(resource, repository, sourceType, sourceId, sourceRevision);
}

export function routeEvent(eventName, payload, repository) {
  if (!payload || !repository) return { action: "ignore", reason: "Missing event data." };

  if (eventName === "workflow_dispatch") {
    const internal = payload.inputs?.internal === "true" && isTrustedFactoryActor(payload.sender);
    if (!isAuthorizedActor(payload.sender) && !internal) {
      return { action: "ignore", reason: "Manual dispatch is restricted to melkio." };
    }
    if (!internal &&
      (payload.inputs?.initialize_only === true || payload.inputs?.initialize_only === "true")) {
      return { action: "initialize" };
    }
    return { action: internal && payload.inputs?.resource_number
      ? "reconcile-resource"
      : "reconcile" };
  }

  if (eventName === "schedule") return { action: "reconcile" };

  if (eventName === "issues") {
    const issue = payload.issue;
    if (payload.action === "opened" && !isAuthorizedActor(payload.sender) &&
      !isTrustedFactoryActor(payload.sender)) {
      const stage = resourceLabels(issue).find((label) => label.startsWith("stage:"));
      if (stage) {
        return { action: "remove-label", resourceNumber: issue.number, label: stage };
      }
    }
    if (payload.action === "labeled" && !payload.label?.name?.startsWith("stage:")) {
      return { action: "ignore", reason: "Non-phase labels do not start a factory step." };
    }
    if (payload.action === "labeled" && payload.label?.name?.startsWith("stage:")) {
      if (!isAuthorizedActor(payload.sender) && !isTrustedFactoryActor(payload.sender)) {
        return {
          action: "remove-label",
          resourceNumber: issue?.number,
          label: payload.label.name,
        };
      }
      if (isTrustedFactoryActor(payload.sender)) {
        return { action: "ignore", reason: "Factory labels are chained explicitly." };
      }
      if (!STAGES.includes(payload.label.name) || payload.label.name.startsWith("stage:review")) {
        return { action: "remove-label", resourceNumber: issue?.number, label: payload.label.name };
      }
    }
    if (!["opened", "labeled", "reopened"].includes(payload.action)) {
      return { action: "reconcile" };
    }
    if (!isAuthorizedActor(payload.sender)) {
      return { action: "ignore", reason: "Issue event is not from an authorized actor." };
    }
    return routeCurrentState(
      issue,
      repository,
      `issues.${payload.action}`,
      payload.label?.id ?? issue?.number,
      issue?.updated_at,
    );
  }

  if (eventName === "issue_comment") {
    const issue = payload.issue;
    const comment = payload.comment;
    if (payload.action !== "created") {
      return { action: "ignore", reason: "Only newly created comments are inputs." };
    }
    if (!isAuthorizedActor(comment?.user)) {
      return deleteCommentPlan(eventName, payload, comment);
    }
    if (issue?.pull_request) {
      if (!parseFactoryCommand(comment?.body)) {
        return { action: "ignore", reason: "No new /factory command." };
      }
      if (payload.pull_request) {
        return routeFollowUp(payload.pull_request, comment, repository, "issue_comment");
      }
      return {
        action: "reconcile-resource",
        resourceType: "pull_request",
        resourceNumber: issue.number,
      };
    }

    const state = issueStep(issue, resourceLabels(issue));
    if (state.error || !["discovery", "design"].includes(state.step)) {
      return { action: "ignore", reason: "Issue comments only resume discovery or design." };
    }
    return makeRoute(state.step, issue, "issue_comment", comment.id, contentRevision(comment.body));
  }

  if (eventName === "pull_request") {
    const pullRequest = payload.pull_request;
    if (payload.action === "opened" && !isAuthorizedActor(payload.sender) &&
      !isTrustedFactoryActor(payload.sender)) {
      const stage = resourceLabels(pullRequest).find((label) => label.startsWith("stage:"));
      if (stage) {
        return { action: "remove-label", resourceNumber: pullRequest.number, label: stage };
      }
    }
    if (payload.action === "labeled" && !payload.label?.name?.startsWith("stage:")) {
      return { action: "ignore", reason: "Non-phase labels do not start a factory step." };
    }
    if (payload.action === "labeled" && payload.label?.name?.startsWith("stage:")) {
      if (!isAuthorizedActor(payload.sender) && !isTrustedFactoryActor(payload.sender)) {
        return {
          action: "remove-label",
          resourceNumber: pullRequest?.number,
          label: payload.label.name,
        };
      }
      if (isTrustedFactoryActor(payload.sender)) {
        return { action: "ignore", reason: "Factory labels are chained explicitly." };
      }
      if (!STAGES.includes(payload.label.name) || !PR_STEPS[payload.label.name]) {
        return { action: "remove-label", resourceNumber: pullRequest?.number, label: payload.label.name };
      }
    }
    if (!["opened", "reopened", "synchronize", "labeled"].includes(payload.action)) {
      return { action: "reconcile" };
    }
    if (payload.action !== "synchronize" && !isAuthorizedActor(payload.sender)) {
      return { action: "ignore", reason: "Pull request event is not authorized." };
    }
    const route = routeCurrentState(
      pullRequest,
      repository,
      `pull_request.${payload.action}`,
      pullRequest?.head?.sha ?? pullRequest?.number,
      pullRequest?.updated_at,
    );
    if (route.action === "dispatch" && route.step !== "review") {
      return { action: "ignore", reason: "Only stage:review resumes on pull request updates." };
    }
    return route;
  }

  if (eventName === "pull_request_review_comment") {
    const comment = payload.comment;
    if (!isAuthorizedActor(comment?.user)) {
      return deleteCommentPlan(eventName, payload, comment);
    }
    const pullRequest = payload.pull_request;
    if (payload.action !== "created" || !parseFactoryCommand(comment?.body)) {
      return { action: "ignore", reason: "No new /factory command." };
    }
    return routeFollowUp(pullRequest, comment, repository, "review_comment");
  }

  if (eventName === "pull_request_review") {
    const review = payload.review;
    if (payload.action !== "submitted") {
      return { action: "ignore", reason: "Pending reviews are not inputs." };
    }
    if (!isAuthorizedActor(review?.user)) {
      if (isTrustedFactoryActor(review?.user)) return { action: "ignore" };
      return {
        action: "dismiss-review",
        resourceNumber: payload.pull_request?.number,
        reviewId: review?.id,
      };
    }
    if (!parseFactoryCommand(review?.body)) {
      return { action: "ignore", reason: "No submitted /factory command." };
    }
    return routeFollowUp(payload.pull_request, review, repository, "review");
  }

  return { action: "ignore", reason: "Unsupported event." };
}

function routeFollowUp(pullRequest, source, repository, sourceType) {
  if (!pullRequest || !isOpen(pullRequest) || !isTrustedPullRequest(pullRequest, repository)) {
    return { action: "ignore", reason: "Pull request is closed or untrusted." };
  }
  const stages = resourceLabels(pullRequest).filter((label) => label.startsWith("stage:"));
  if (stages.length > 1 || stages.some((stage) => stage !== "stage:review")) {
    return { action: "ignore", reason: "Follow-up requires an open PR in stage:review." };
  }
  return makeRoute(
    "follow-up",
    pullRequest,
    sourceType,
    source.id,
    contentRevision(source.body),
  );
}

export function makeContract(route, repository) {
  if (route?.action !== "dispatch" || !WORKFLOWS[route.step]) {
    throw new Error("Cannot create a contract for a non-dispatch route.");
  }
  const value = {
    schema_version: 1,
    repository,
    resource: route.resource,
    step: route.step,
    event: route.source,
    expected: route.expected,
    checkpoint: { key: "", status: "waiting" },
    result: { status: "waiting" },
  };
  value.checkpoint.key = idempotencyKey(value);
  return value;
}

function idempotencyKey(contract) {
  return createHash("sha256")
    .update(JSON.stringify([
      contract.schema_version,
      contract.repository,
      contract.resource.type,
      contract.resource.number,
      contract.step,
      contract.event.type,
      contract.event.id,
      contract.event.revision,
      contract.expected.sha,
      contract.expected.state_revision,
    ]))
    .digest("hex");
}

export function validateContract(contract, expectedRepository) {
  const validStatus = (status) => ["waiting", "completed", "blocked", "failed"].includes(status);
  const validEventTypes = [
    "issues.opened",
    "issues.labeled",
    "issues.reopened",
    "issue_comment",
    "review_comment",
    "review",
    "pull_request.opened",
    "pull_request.reopened",
    "pull_request.synchronize",
    "pull_request.labeled",
    "manual",
    "reconcile",
    "next",
    "event",
  ];
  if (contract?.schema_version !== 1 ||
    contract.repository !== expectedRepository ||
    !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(contract.repository) ||
    !["issue", "pull_request"].includes(contract.resource?.type) ||
    !Number.isInteger(contract.resource?.number) ||
    contract.resource.number < 1 ||
    !WORKFLOWS[contract.step] ||
    !validEventTypes.includes(contract.event?.type) ||
    !/^[A-Za-z0-9_.:-]{1,200}$/.test(contract.event.id) ||
    !/^[A-Za-z0-9_.:-]{1,128}$/.test(contract.event.revision ?? "") ||
    (contract.expected?.sha !== null &&
      !/^[a-f0-9]{40}$/i.test(contract.expected?.sha ?? "")) ||
    !/^[a-f0-9]{64}$/.test(contract.expected?.state_revision ?? "") ||
    !validStatus(contract.checkpoint?.status) ||
    !/^[a-f0-9]{64}$/.test(contract.checkpoint?.key ?? "") ||
    !validStatus(contract.result?.status)) {
    throw new Error("Invalid factory contract.");
  }
  if (contract.resource.type === "issue" && contract.expected.sha !== null) {
    throw new Error("Issues must not contain a pull request SHA.");
  }
  if (contract.resource.type === "pull_request" && !/^[a-f0-9]{40}$/i.test(contract.expected.sha ?? "")) {
    throw new Error("Pull request contracts require an expected head SHA.");
  }
  if ((["review", "lessons", "follow-up"].includes(contract.step)) !==
    (contract.resource.type === "pull_request")) {
    throw new Error("Step does not match the resource type.");
  }
  if (contract.checkpoint.key !== idempotencyKey(contract)) {
    throw new Error("Factory contract idempotency key does not match its inputs.");
  }
  return contract;
}
