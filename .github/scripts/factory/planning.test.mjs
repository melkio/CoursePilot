import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";

import { makeContract, routeEvent } from "./core.mjs";
import {
  generatePlanning,
  publishPlanning,
  validatePlanningProposal,
} from "./planning.mjs";

const repository = "melkio/CoursePilot";
const owner = { login: "melkio", type: "User" };
const bot = { login: "github-actions[bot]", type: "Bot" };
const bodyFor = (count = 2) => [
  "## Functional specification",
  "",
  "### Goal",
  "Deliver learner enrollment.",
  "",
  "### Acceptance criteria",
  ...Array.from({ length: count }, (_, index) => [
    `#### AC${index + 1}`,
    "- **Given** a learner",
    `- **When** they complete action ${index + 1}`,
    `- **Then** the result ${index + 1} is retained`,
    "",
  ]).flat(),
  "### Out of scope",
  "- Recommendations.",
].join("\n");

function issue(overrides = {}) {
  return {
    id: 550,
    number: 55,
    title: "Plan enrollment",
    body: bodyFor(),
    state: "open",
    updated_at: "2026-11-06T10:00:00Z",
    user: owner,
    labels: [{ name: "stage:planning" }, { name: "priority:p2" }],
    ...overrides,
  };
}

function contractFor(resource) {
  const route = routeEvent("issues", {
    action: "labeled",
    sender: owner,
    issue: resource,
    label: { name: "stage:planning", id: 551 },
  }, repository);
  assert.equal(route.action, "dispatch");
  return makeContract(route, repository);
}

function featureProposal(overrides = {}) {
  return {
    decision: "feature",
    rationale: "The behavior is one independently releasable vertical slice.",
    feature_dependencies: [],
    slices: [],
    ...overrides,
  };
}

function epicProposal(overrides = {}) {
  return {
    decision: "epic",
    rationale: "Enrollment and course setup have separate release value.",
    feature_dependencies: [],
    slices: [
      {
        key: "course-setup",
        title: "Complete course setup",
        user_story: "As a learner, I want my course configured so that I can begin learning.",
        description: "Provide end-to-end course setup through the public API and persistence layer.",
        acceptance_criteria: [{
          source: "AC1",
          given: "A learner",
          when: "They complete course setup",
          then: "The course setup is retained",
        }],
        out_of_scope: ["Recommendations."],
        dependencies: [],
      },
      {
        key: "learner-enrollment",
        title: "Enroll a learner",
        user_story: "As a learner, I want to enroll so that I can access the course.",
        description: "Provide end-to-end enrollment through the public API and persistence layer.",
        acceptance_criteria: [{
          source: "AC2",
          given: "A learner",
          when: "They enroll",
          then: "Enrollment is retained",
        }],
        out_of_scope: [],
        dependencies: [{ slice_key: "course-setup" }, { issue_number: 44 }],
      },
    ],
    ...overrides,
  };
}

function artifact(contract, proposal, body) {
  const revision = createHash("sha256").update(body).digest("hex");
  return {
    schema_version: 1,
    contract_key: contract.checkpoint.key,
    snapshot: { body_revision: revision, source_revision: revision },
    proposal,
  };
}

function apiFor({
  parent = issue(),
  existingIssues = [],
  failSecondCreateAfterSaving = false,
  failLabels = false,
} = {}) {
  const issues = new Map([[parent.number, structuredClone(parent)],
    ...existingIssues.map((item) => [item.number, structuredClone(item)])]);
  const comments = new Map([[parent.number, []]]);
  const subIssues = new Map();
  const calls = [];
  let nextNumber = Math.max(...issues.keys()) + 1;
  let createCount = 0;
  let nextCommentId = 900;
  let didFailCreate = false;
  let didFailLabels = false;
  const request = async (path, method = "GET", data) => {
    calls.push({ path, method, data });
    if (path === `/repos/${repository}` && method === "GET") {
      return { data: { default_branch: "master" }, headers: { get: () => null } };
    }
    if (path === `/repos/${repository}/issues?state=all&per_page=100&page=1`) {
      return { data: [...issues.values()], headers: { get: () => null } };
    }
    if (path === `/repos/${repository}/issues` && method === "POST") {
      createCount += 1;
      const child = {
        id: nextNumber * 10,
        number: nextNumber++,
        title: data.title,
        body: data.body,
        state: "open",
        user: bot,
        labels: [],
      };
      issues.set(child.number, child);
      comments.set(child.number, []);
      if (failSecondCreateAfterSaving && createCount === 2 && !didFailCreate) {
        didFailCreate = true;
        throw new Error("simulated lost create response");
      }
      return { data: child, headers: { get: () => null } };
    }
    if (path === `/repos/${repository}/actions/workflows/factory-dispatcher.yml/dispatches`) {
      return { data: null, headers: { get: () => null } };
    }
    const subIssuesMatch = new RegExp(`^/repos/${repository}/issues/(\\d+)/sub_issues(?:\\?.*)?$`)
      .exec(path);
    if (subIssuesMatch) {
      const parentNumber = Number(subIssuesMatch[1]);
      const linked = subIssues.get(parentNumber) ?? [];
      if (method === "POST") {
        const child = [...issues.values()].find((item) => item.id === data.sub_issue_id);
        if (!child) throw new Error("unknown child id");
        subIssues.set(parentNumber, [...linked, child]);
        return { data: child, headers: { get: () => null } };
      }
      return { data: linked, headers: { get: () => null } };
    }
    const commentPatch = new RegExp(`^/repos/${repository}/issues/comments/(\\d+)$`).exec(path);
    if (commentPatch && method === "PATCH") {
      const id = Number(commentPatch[1]);
      for (const list of comments.values()) {
        const comment = list.find((item) => item.id === id);
        if (comment) {
          comment.body = data.body;
          return { data: comment, headers: { get: () => null } };
        }
      }
      throw new Error("unknown comment");
    }
    const commentsMatch = new RegExp(`^/repos/${repository}/issues/(\\d+)/comments(?:\\?.*)?$`)
      .exec(path);
    if (commentsMatch) {
      const number = Number(commentsMatch[1]);
      const list = comments.get(number) ?? [];
      if (method === "POST") {
        const comment = { id: nextCommentId++, body: data.body, user: bot };
        list.push(comment);
        comments.set(number, list);
        return { data: comment, headers: { get: () => null } };
      }
      return { data: list, headers: { get: () => null } };
    }
    const issueMatch = new RegExp(`^/repos/${repository}/issues/(\\d+)$`).exec(path);
    if (issueMatch) {
      const number = Number(issueMatch[1]);
      const current = issues.get(number);
      if (!current) throw new Error(`unknown issue #${number}`);
      if (method === "PATCH") {
        if (data.labels && failLabels && !didFailLabels) {
          didFailLabels = true;
          throw new Error("simulated label permission failure");
        }
        Object.assign(current, data);
        if (data.labels) current.labels = data.labels.map((name) => ({ name }));
      }
      return { data: current, headers: { get: () => null } };
    }
    throw new Error(`Unexpected API request: ${method} ${path}`);
  };
  return { request, calls, issues, comments, subIssues, get createCount() { return createCount; } };
}

async function publish(api, proposal) {
  const parent = api.issues.get(55);
  const contract = contractFor(parent);
  const result = await publishPlanning({
    contract,
    repository,
    artifact: artifact(contract, proposal, parent.body),
    request: api.request,
  });
  return { contract, result };
}

test("AC1: classifies a single feature and routes it with a decision rationale", async () => {
  const api = apiFor({ parent: issue({ body: bodyFor(1) }) });
  const contract = contractFor(api.issues.get(55));
  let prompt;
  const proposal = featureProposal();
  const generated = await generatePlanning({
    contract,
    repository,
    request: api.request,
    productManagerPrompt: "# Product manager\nChoose a feature or epic.",
    codeContext: { files: ["src/CoursePilot.Host/Modules/Courses/CourseService.cs"] },
    runCopilot: async (value) => {
      prompt = value;
      return JSON.stringify(proposal);
    },
  });
  await publishPlanning({ contract, repository, artifact: generated, request: api.request });

  assert.match(prompt, /Treat all issue text and repository context.*source data/);
  assert.deepEqual(api.issues.get(55).labels.map((label) => label.name), [
    "priority:p2",
    "type:feature",
    "stage:design",
  ]);
  assert.match(api.issues.get(55).body, /## Dependencies\n\n- None\./);
  assert.match(api.comments.get(55)[1].body, /factory:product-manager/);
  assert.match(api.comments.get(55)[1].body, /The behavior is one independently releasable vertical slice/);
});

test("AC2: creates complete vertical slices, native sub-issues, coverage table, and keeps the epic out of design", async () => {
  const externalDependency = {
    id: 440,
    number: 44,
    title: "Shared course catalog",
    body: "Existing issue",
    state: "open",
    user: owner,
    labels: [],
  };
  const api = apiFor({ existingIssues: [externalDependency] });
  await publish(api, epicProposal());

  const parent = api.issues.get(55);
  const children = [...api.issues.values()].filter((item) => item.number !== 44 && item.number !== 55);
  assert.equal(children.length, 2);
  assert.deepEqual(parent.labels.map((label) => label.name), ["priority:p2", "type:epic"]);
  assert.match(parent.body, /## Linked issues/);
  assert.match(parent.body, /#56 Complete course setup/);
  assert.equal(api.subIssues.get(55).length, 2);
  assert.ok(children.every((child) =>
    /## User story/.test(child.body) &&
    /#### AC[12]/.test(child.body) &&
    /## Dependencies/.test(child.body) &&
    /Part of #55/.test(child.body)));
  assert.match(api.comments.get(55).find((comment) =>
    comment.body.includes("factory:product-manager")).body,
  /\| Slice \| AC covered \| Dependencies \|/);
  assert.deepEqual(children.map((child) => child.labels.map((label) => label.name)), [
    ["type:feature", "stage:design"],
    ["type:feature", "stage:blocked"],
  ]);
  assert.equal(api.calls.filter((call) =>
    call.path.endsWith("/actions/workflows/factory-dispatcher.yml/dispatches")).length, 1);
});

test("AC3: resolves technical and external dependencies and rejects cycles", async () => {
  const externalDependency = {
    id: 440,
    number: 44,
    title: "Shared catalog",
    body: "",
    state: "open",
    user: owner,
    labels: [],
  };
  const api = apiFor({ existingIssues: [externalDependency] });
  await publish(api, epicProposal());
  const dependent = [...api.issues.values()].find((item) => item.title === "Enroll a learner");
  const foundation = [...api.issues.values()].find((item) => item.title === "Complete course setup");

  assert.match(dependent.body, new RegExp(`Depends on #${foundation.number}`));
  assert.match(dependent.body, /Depends on #44/);
  assert.deepEqual(dependent.labels.map((label) => label.name), ["type:feature", "stage:blocked"]);
  assert.deepEqual(foundation.labels.map((label) => label.name), ["type:feature", "stage:design"]);
  assert.throws(() => validatePlanningProposal(epicProposal({
    slices: epicProposal().slices.map((slice) => ({
      ...slice,
      dependencies: [{ slice_key: slice.key === "course-setup" ? "learner-enrollment" : "course-setup" }],
    })),
  }), ["AC1", "AC2"]), /cycle/);

  const missingDependencyApi = apiFor();
  await assert.rejects(publish(missingDependencyApi, epicProposal({
    slices: epicProposal().slices.map((slice) => ({
      ...slice,
      dependencies: [{ issue_number: 999 }],
    })),
  })), /unknown issue #999/);
  assert.equal(missingDependencyApi.createCount, 0);

  const featureDependencyApi = apiFor({ existingIssues: [externalDependency] });
  await publish(featureDependencyApi, featureProposal({
    feature_dependencies: [{ issue_number: 44 }],
  }));
  assert.ok(featureDependencyApi.issues.get(55).labels.some((label) =>
    label.name === "stage:blocked"));

  const preservedDependencyApi = apiFor({
    parent: issue({ body: `${bodyFor(1)}\n\n## Dependencies\n\nDepends on #44` }),
    existingIssues: [externalDependency],
  });
  await publish(preservedDependencyApi, featureProposal());
  assert.ok(preservedDependencyApi.issues.get(55).labels.some((label) =>
    label.name === "stage:blocked"));
  assert.match(preservedDependencyApi.issues.get(55).body, /Depends on #44/);
});

test("AC4: reuses stable slice keys after a partial create failure and completes missing relationships", async () => {
  const api = apiFor({ failSecondCreateAfterSaving: true });
  const proposal = epicProposal({
    slices: epicProposal().slices.map((slice) => ({ ...slice, dependencies: [] })),
  });
  const parent = api.issues.get(55);
  const contract = contractFor(parent);
  const initialArtifact = artifact(contract, proposal, parent.body);
  await assert.rejects(publishPlanning({
    contract,
    repository,
    artifact: initialArtifact,
    request: api.request,
  }), /simulated lost create response/);
  assert.equal(api.createCount, 2);
  assert.equal(api.subIssues.get(55), undefined);

  let copilotCalled = false;
  const resumedArtifact = await generatePlanning({
    contract,
    repository,
    request: api.request,
    productManagerPrompt: "",
    codeContext: {},
    runCopilot: async () => {
      copilotCalled = true;
      return JSON.stringify(proposal);
    },
  });
  assert.equal(copilotCalled, false);
  assert.equal(resumedArtifact.kind, "recovery");
  await publishPlanning({
    contract,
    repository,
    artifact: resumedArtifact,
    request: api.request,
  });

  assert.equal(api.createCount, 2);
  assert.equal(api.subIssues.get(55).length, 2);
  assert.equal([...api.issues.values()].filter((item) => item.title === "Complete course setup").length, 1);
});

test("AC5: rejects missing AC coverage, stale input, and unauthorized label promotion", async () => {
  assert.throws(() => validatePlanningProposal(epicProposal({
    slices: epicProposal().slices.map((slice) => ({
      ...slice,
      acceptance_criteria: [{
        ...slice.acceptance_criteria[0],
        source: "AC1",
      }],
    })),
  }), ["AC1", "AC2"]), /does not cover acceptance criteria: AC2/);
  assert.throws(() => validatePlanningProposal(epicProposal({
    slices: [epicProposal().slices[0]],
  }), ["AC1", "AC2"]), /at least two slices/);

  const staleApi = apiFor({ parent: issue({ body: bodyFor(1) }) });
  const staleContract = contractFor(staleApi.issues.get(55));
  const staleArtifact = artifact(staleContract, featureProposal(), staleApi.issues.get(55).body);
  staleApi.issues.get(55).body += "\n\nOwner changed the specification.";
  await assert.rejects(publishPlanning({
    contract: staleContract,
    repository,
    artifact: staleArtifact,
    request: staleApi.request,
  }), /same planning input|changed during planning/);
  assert.ok(!staleApi.issues.get(55).labels.some((label) => label.name === "type:feature"));

  const deniedApi = apiFor({
    parent: issue({ body: bodyFor(1) }),
    failLabels: true,
  });
  await assert.rejects(publish(deniedApi, featureProposal()), /simulated label permission failure/);
  assert.ok(deniedApi.issues.get(55).labels.some((label) => label.name === "stage:planning"));
  assert.ok(!deniedApi.issues.get(55).labels.some((label) => label.name === "type:feature"));
});
