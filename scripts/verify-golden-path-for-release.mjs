#!/usr/bin/env node
// Release gate: refuse to publish a tag unless the golden path has passed on the exact commit the tag points at.
// Every Deluno release is held for it, release candidates included (docs/golden-path.md). The golden path is a run
// of the real product on a clean machine, installed from that commit's own build (a manual ci.yml run makes the
// Windows installer for any commit). Whoever drives it records the result as a GitHub commit status on the commit:
//
//   gh api repos/<owner>/<repo>/statuses/<sha> -f state=success -f context=golden-path \
//     -f description="..." -f target_url="<link to the evidence>"
//
// The gate passes only when the newest status with the context `golden-path` on this commit is `success`. A later
// `failure`, `error` or `pending` for the same context withdraws an earlier `success`, so the record always says
// how the latest run went. It does not wait: a tag is made after the run has passed.
//
// A pass also carries over (James, 16 Oct 2026): a `success` on an ancestor commit X counts for the tagged commit Y
// when no file that ships differs between X and Y (scripts/ship-paths.mjs, the one rule; a different version in
// Directory.Build.props always differs). The nearest ancestor with any golden-path status decides, so a later
// failure on identical shipped files is never skipped. The checkout needs the full history (fetch-depth: 0).
//
// Usage (in release.yml):  node scripts/verify-golden-path-for-release.mjs
//   env GH_TOKEN (statuses: read), GITHUB_REPOSITORY, GITHUB_SHA (else `git rev-parse HEAD`)
// Options: --sha <sha>
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { sameShippedFiles } from "./ship-paths.mjs";

export const GOLDEN_PATH_CONTEXT = "golden-path";

export const NO_PASSING_RUN =
  "This commit has no passing golden-path run. Run the golden path on this exact build (see docs/golden-path.md), then re-run the release.";

// The newest status for a context. `statuses` are commit statuses as the API returns them (id, context, state,
// created_at, ...). Ids rise with time, so they settle two statuses created in the same second.
export function latestStatus(statuses, context = GOLDEN_PATH_CONTEXT) {
  let latest = null;
  for (const status of statuses) {
    if (status.context !== context) continue;
    if (!latest || Date.parse(status.created_at) > Date.parse(latest.created_at)
      || (status.created_at === latest.created_at && status.id > latest.id)) {
      latest = status;
    }
  }
  return latest;
}

// Judges a commit's statuses. Returns the reasons the golden path does not count; empty means it passed.
export function evaluateStatuses(statuses, context = GOLDEN_PATH_CONTEXT) {
  const latest = latestStatus(statuses, context);
  if (!latest) return [`no status with the context "${context}" is recorded`];
  if (latest.state !== "success") {
    return [`the latest "${context}" status is ${latest.state}${latest.description ? ` (${latest.description})` : ""}`];
  }
  return [];
}

// How far back to look for a pass to carry over. Commits further back than this are never used.
export const MAX_CARRY_COMMITS = 200;

// Finds the golden-path pass that counts for `head`: its own, else the nearest ancestor's with identical shipped
// files. Returns { ok: true, sha, status, carried, reason } or { ok: false, problems }.
// listStatuses(sha) returns that commit's statuses; git(args) runs git and throws on a non-zero exit.
export function findGoldenPathPass(head, { listStatuses, git, maxCommits = MAX_CARRY_COMMITS }) {
  const own = listStatuses(head);
  const ownProblems = evaluateStatuses(own);
  if (ownProblems.length === 0) return { ok: true, sha: head, status: latestStatus(own), carried: false, reason: "the same commit" };
  // A status recorded on this very commit that is not a success (a failure) is the answer; an older pass cannot override it.
  if (latestStatus(own)) return { ok: false, problems: ownProblems };

  let ancestors;
  try {
    ancestors = git(["rev-list", `--max-count=${maxCommits + 1}`, head]).split("\n").map((line) => line.trim()).filter(Boolean).slice(1);
  } catch (error) {
    return { ok: false, problems: [...ownProblems, `the history before ${head} could not be read (${error.message.split("\n")[0]}); fetch the full history`] };
  }
  for (const candidate of ancestors) {
    const same = sameShippedFiles(candidate, head, git);
    if (!same.ok) {
      return { ok: false, problems: [...ownProblems, `no earlier commit with the same shipped files has a golden-path pass (stopped at ${candidate}: ${same.reason})`] };
    }
    const statuses = listStatuses(candidate);
    const latest = latestStatus(statuses);
    if (!latest) continue;
    const problems = evaluateStatuses(statuses);
    if (problems.length) return { ok: false, problems: [...ownProblems, `the nearest earlier commit with the same shipped files, ${candidate}, did not pass: ${problems.join("; ")}`] };
    return { ok: true, sha: candidate, status: latest, carried: true, reason: same.reason };
  }
  return { ok: false, problems: [...ownProblems, `none of the ${ancestors.length} commits before it has a golden-path run`] };
}

function listStatuses(repository, sha) {
  // One compact object per line, so a paginated listing needs no JSON array stitching.
  const output = execFileSync(
    "gh",
    [
      "api", "--paginate", `repos/${repository}/commits/${sha}/statuses?per_page=100`,
      "--jq", ".[] | {id, context, state, description, target_url, created_at, creator: .creator.login}",
    ],
    { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 },
  );
  return output.split("\n").filter((line) => line.trim()).map((line) => JSON.parse(line));
}

function main() {
  const argv = process.argv.slice(2);
  const option = (flag) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : undefined);
  const repository = process.env.GITHUB_REPOSITORY;
  if (!repository) throw new Error("GITHUB_REPOSITORY must be set.");
  const sha = option("--sha") || process.env.GITHUB_SHA || execFileSync("git", ["rev-parse", "HEAD^{commit}"], { encoding: "utf8" }).trim();
  if (!/^[0-9a-f]{40}$/.test(sha)) throw new Error(`Not a full commit SHA: ${sha}`);
  console.log(`Release gate: the golden path must already have passed on ${sha}.`);

  const git = (args) => execFileSync("git", args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });
  const found = findGoldenPathPass(sha, { listStatuses: (commit) => listStatuses(repository, commit), git });
  if (found.ok) {
    const passed = found.status;
    if (found.carried) {
      console.log(`PASS (carried over): "${GOLDEN_PATH_CONTEXT}" is success on ${found.sha}, an earlier commit, and the shipped files are identical between ${found.sha} and ${sha} (${found.reason}; only tests, scripts, docs or CI files changed).`);
    } else {
      console.log(`PASS: "${GOLDEN_PATH_CONTEXT}" is success on ${sha}.`);
    }
    console.log(`  Recorded by ${passed.creator || "the run"} at ${passed.created_at}.`);
    if (passed.description) console.log(`  ${passed.description}`);
    if (passed.target_url) console.log(`  Evidence: ${passed.target_url}`);
    return;
  }
  const problems = found.problems;

  console.error(`::error::${NO_PASSING_RUN}`);
  for (const problem of problems) console.error(`  - ${problem}`);
  process.exit(1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
}
