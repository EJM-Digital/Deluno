// Opens a defect issue for a nightly finding, or adds a comment with the new run link to the open issue that already has the
// same title, so one problem found night after night is one issue, not one per night (#1377, #1379, #1381 and #1383 were the same).
// Run: node scripts/report-nightly-issue.mjs --title "<title>" --body "<text>" --run-url <url> [--label "<label>"]
// Needs GH_TOKEN and GITHUB_REPOSITORY; GITHUB_API_URL defaults to https://api.github.com. Test: scripts/report-nightly-issue.test.mjs
import { pathToFileURL } from 'node:url';

/** Finds the open issue with exactly this title (pull requests are not issues here), looking through every page. */
export async function findOpenIssue({ api, repo, token, title, label, fetchImpl = fetch }) {
  for (let page = 1; page <= 20; page++) {
    const query = new URLSearchParams({ state: 'open', per_page: '100', page: String(page) });
    if (label) query.set('labels', label);
    const response = await fetchImpl(`${api}/repos/${repo}/issues?${query}`, { headers: headers(token) });
    if (!response.ok) throw new Error(`GitHub answered ${response.status} listing issues`);
    const issues = await response.json();
    const found = issues.find((issue) => !issue.pull_request && issue.title === title);
    if (found) return found;
    if (issues.length < 100) return null;
  }
  return null;
}

function headers(token) {
  return { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'Content-Type': 'application/json' };
}

/** Comments on the open issue with this title, or opens one when none is open. Returns what it did. */
export async function reportNightlyIssue({ api = 'https://api.github.com', repo, token, title, body, runUrl, label, fetchImpl = fetch }) {
  const existing = await findOpenIssue({ api, repo, token, title, label, fetchImpl });
  if (existing) {
    const response = await fetchImpl(`${api}/repos/${repo}/issues/${existing.number}/comments`, {
      method: 'POST',
      headers: headers(token),
      body: JSON.stringify({ body: `It happened again: ${runUrl}` })
    });
    if (!response.ok) throw new Error(`GitHub answered ${response.status} adding a comment to #${existing.number}`);
    return { action: 'commented', number: existing.number };
  }
  const response = await fetchImpl(`${api}/repos/${repo}/issues`, {
    method: 'POST',
    headers: headers(token),
    body: JSON.stringify({ title, body, labels: label ? [label] : [] })
  });
  if (!response.ok) throw new Error(`GitHub answered ${response.status} opening the issue`);
  const created = await response.json();
  return { action: 'opened', number: created.number };
}

function argument(name) {
  const index = process.argv.indexOf(`--${name}`);
  return index > 0 ? process.argv[index + 1] : undefined;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const title = argument('title');
  const body = argument('body');
  const runUrl = argument('run-url');
  if (!title || !body || !runUrl || !process.env.GH_TOKEN || !process.env.GITHUB_REPOSITORY) {
    console.error('Usage: GH_TOKEN=... GITHUB_REPOSITORY=owner/repo node scripts/report-nightly-issue.mjs --title <t> --body <b> --run-url <u> [--label <l>]');
    process.exit(2);
  }
  const result = await reportNightlyIssue({
    api: process.env.GITHUB_API_URL ?? 'https://api.github.com',
    repo: process.env.GITHUB_REPOSITORY,
    token: process.env.GH_TOKEN,
    title, body, runUrl,
    label: argument('label')
  });
  console.log(`${result.action} #${result.number}`);
}
