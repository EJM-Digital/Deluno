// Fails when the rules that guard main and the release tags are missing or weakened (GAT-10, #1302).
//
// The repository is public and its rulesets are the only thing between a push and a published release:
//   - main: no deletion, no rewriting, and the `ci-passed` check must pass before a change lands;
//   - main: a second rule nobody can bypass, so history is never rewritten or deleted, not even by the owner;
//   - tags `v*`: only the owner creates, moves or deletes a release tag.
// The rules are judged by what they do, not by their names, so renaming one is fine and emptying one is not.
// Every ruleset must be active (an evaluate-only or disabled one protects nothing).
//
// Usage: node scripts/check-branch-protection.mjs [--repo <owner/name>]
//   GITHUB_TOKEN (optional) is sent when set; GITHUB_REPOSITORY gives the repository in CI.
// Reading the rules needs no special permission on a public repository. GitHub shows the people allowed to bypass a rule only to
// someone who can change it, so a reply without that list is judged on everything else and the check says so.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const requiredCheck = 'ci-passed';
const ownerRole = { actor_id: 5, actor_type: 'RepositoryRole' };

const hasRule = (ruleset, type) => (ruleset.rules ?? []).some((rule) => rule.type === type);
const includes = (ruleset) => ruleset.conditions?.ref_name?.include ?? [];
const excludes = (ruleset) => ruleset.conditions?.ref_name?.exclude ?? [];
const active = (ruleset) => ruleset.enforcement === 'active';

/** True when the ruleset covers the default branch, whichever way it names it. */
const coversMain = (ruleset) => ruleset.target === 'branch'
  && includes(ruleset).some((ref) => ['~DEFAULT_BRANCH', 'refs/heads/main', '~ALL', 'refs/heads/**', 'refs/heads/*'].includes(ref))
  && excludes(ruleset).length === 0;

const coversReleaseTags = (ruleset) => ruleset.target === 'tag'
  && includes(ruleset).some((ref) => ['refs/tags/v*', 'refs/tags/**', 'refs/tags/*', '~ALL'].includes(ref))
  && excludes(ruleset).length === 0;

/** Who may bypass, or null when the reply does not say. */
const bypassers = (ruleset) => (Array.isArray(ruleset.bypass_actors) ? ruleset.bypass_actors : null);

const onlyOwner = (ruleset) => {
  const list = bypassers(ruleset);
  return list === null || list.every((actor) => actor.actor_type === ownerRole.actor_type && actor.actor_id === ownerRole.actor_id);
};

const nobodyBypasses = (ruleset) => {
  const list = bypassers(ruleset);
  return list === null || list.length === 0;
};

const requiresCi = (ruleset) => (ruleset.rules ?? []).some((rule) => rule.type === 'required_status_checks'
  && (rule.parameters?.required_status_checks ?? []).some((check) => check.context === requiredCheck));

/** The problems with these rulesets (each ruleset in full, as `GET /repos/{owner}/{repo}/rulesets/{id}` gives it), in everyday words. */
export function problems(rulesets) {
  const live = rulesets.filter(active);
  const out = [];

  const guardingMain = live.filter(coversMain);
  const noRewrite = (r) => hasRule(r, 'deletion') && hasRule(r, 'non_fast_forward');
  if (!guardingMain.some((r) => noRewrite(r) && requiresCi(r) && onlyOwner(r))) {
    out.push(`Nothing makes a change to main pass "${requiredCheck}" first: no active rule on main blocks deletion and rewriting, requires the "${requiredCheck}" check, and lets only the repository admin role bypass it.`);
  }
  if (!guardingMain.some((r) => noRewrite(r) && nobodyBypasses(r))) {
    out.push('Nothing stops main being rewritten or deleted by anyone: no active rule on main blocks deletion and rewriting with no one allowed to bypass it.');
  }
  if (!live.filter(coversReleaseTags).some((r) => hasRule(r, 'creation') && hasRule(r, 'update') && hasRule(r, 'deletion') && onlyOwner(r))) {
    out.push('Anyone with write access could make, move or delete a release tag: no active rule on the v* tags blocks creating, updating and deleting them for everyone but the repository admin role.');
  }

  const inactive = rulesets.filter((r) => !active(r)).map((r) => r.name);
  if (inactive.length > 0) out.push(`These rules are not active, so they protect nothing: ${inactive.join(', ')}.`);
  return out;
}

/** Fetches every ruleset of the repository in full. `fetchJson(path)` returns the parsed reply or throws. */
export async function fetchRulesets(repo, fetchJson) {
  const list = await fetchJson(`/repos/${repo}/rulesets`);
  if (!Array.isArray(list)) throw new Error(`GitHub's list of rulesets for ${repo} was not a list.`);
  const full = [];
  for (const entry of list) full.push(await fetchJson(`/repos/${repo}/rulesets/${entry.id}`));
  return full;
}

/** The rulesets in a recording of GitHub's replies (tests/fixtures/recorded/github/.../repository-rulesets.json). */
export function recordedRulesets(path) {
  const recording = JSON.parse(readFileSync(path, 'utf8'));
  return recording.exchanges.filter((e) => /\/rulesets\/\d+$/.test(e.request.path)).map((e) => e.reply.body);
}

function githubJson(token) {
  return async (path) => {
    const headers = { accept: 'application/vnd.github+json', 'x-github-api-version': '2022-11-28', 'user-agent': 'deluno-branch-protection-check' };
    if (token) headers.authorization = `Bearer ${token}`;
    const reply = await fetch(`https://api.github.com${path}`, { headers });
    if (!reply.ok) throw new Error(`GitHub answered ${reply.status} for ${path}.`);
    return reply.json();
  };
}

async function main() {
  const flag = process.argv.indexOf('--repo');
  const repo = flag >= 0 ? process.argv[flag + 1] : process.env.GITHUB_REPOSITORY;
  if (!repo) {
    console.error('Name the repository: --repo <owner/name>, or set GITHUB_REPOSITORY.');
    process.exit(1);
  }
  let rulesets;
  try {
    rulesets = await fetchRulesets(repo, githubJson(process.env.GITHUB_TOKEN));
  } catch (error) {
    console.error(`Could not read the rules of ${repo}: ${error.message}`);
    process.exit(1);
  }
  const found = problems(rulesets);
  if (found.length > 0) {
    for (const line of found) console.error(line);
    process.exit(1);
  }
  const unknown = rulesets.some((r) => bypassers(r) === null);
  console.log(`The rules guarding main and the v* tags in ${repo} are in place${unknown ? ' (GitHub did not list who may bypass them for this token, so that part was not judged)' : ''}.`);
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) await main();
