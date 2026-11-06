const repository = process.env.GITHUB_REPOSITORY ?? "";
const token = process.env.GITHUB_TOKEN;
const apiBase = process.env.GITHUB_API_URL ?? "https://api.github.com";
const [owner, repo] = repository.split("/");
const issueNumber = Number(process.env.FACTORY_SMOKE_ISSUE);
const runId = process.env.GITHUB_RUN_ID;
const marker = `<!-- factory-smoke:v1 run=${runId} -->`;

if (!owner || !repo || !token || !Number.isSafeInteger(issueNumber) || issueNumber < 1 || !runId) {
  throw new Error("Invalid smoke publisher configuration.");
}

async function request(path, method = "GET", body) {
  const url = `${apiBase.replace(/\/+$/, "")}/${path.replace(/^\/+/, "")}`;
  const response = await fetch(url, {
    method,
    headers: {
      accept: "application/vnd.github+json",
      authorization: "Bearer " + token,
      "x-github-api-version": "2022-11-28",
      ...(body === undefined ? {} : { "content-type": "application/json" }),
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  if (!response.ok) {
    throw new Error(`GitHub API ${method} ${path} failed with HTTP ${response.status}.`);
  }
  if (response.status === 204) return null;
  return { data: await response.json(), headers: response.headers };
}

async function pages(path) {
  const items = [];
  for (let page = 1; page <= 100; page += 1) {
    const response = await request(`${path}${path.includes("?") ? "&" : "?"}per_page=100&page=${page}`);
    items.push(...response.data);
    if (!response.headers.get("link")?.includes('rel="next"')) return items;
  }
  throw new Error("Issue comment pagination limit reached.");
}

const issueResponse = await request(`/repos/${owner}/${repo}/issues/${issueNumber}`);
const issue = issueResponse.data;
if (issue.pull_request || issue.state !== "open" || issue.user?.login !== "melkio") {
  throw new Error("Smoke output may only be published to an open issue authored by melkio.");
}

const comments = await pages(`/repos/${owner}/${repo}/issues/${issueNumber}/comments`);
const existing = comments.find((comment) =>
  comment.user?.login === "github-actions[bot]" &&
  comment.user?.type === "Bot" &&
  comment.body?.startsWith(marker));
if (existing) {
  process.stdout.write("Smoke output was already published for this run.\n");
} else {
  await request(`/repos/${owner}/${repo}/issues/${issueNumber}/comments`, "POST", {
    body: `${marker}\nFactory smoke test passed for Copilot CLI ${process.env.COPILOT_CLI_VERSION} using \`${process.env.COPILOT_MODEL}\`. Run: ${process.env.GITHUB_SERVER_URL}/${repository}/actions/runs/${runId}`,
  });
  process.stdout.write("Controlled smoke output published.\n");
}
