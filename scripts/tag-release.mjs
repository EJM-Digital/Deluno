#!/usr/bin/env node
// Makes a release tag, only when it is safe to. Run through scripts/tag-release.ps1 -Version <x.y.z[-rc.N[.M]]>.
// Tags are made only this way (AGENTS.md, docs/ga-release-checklist.md). It refuses, saying why in plain words,
// unless: the working tree is clean and HEAD is origin/main; Directory.Build.props and RELEASE_NOTES.md carry the
// version; ci.yml passed on HEAD and really ran the jobs; and the golden path passed on HEAD
// (or on an earlier commit with the same shipped files, scripts/ship-paths.mjs). Then it creates the
// annotated tag v<version> and pushes it, which starts the release.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import {
  checkCi, checkGoldenPath, describeGoldenPath, checkPropsVersion, checkPushed, checkReleaseNotes, checkVersionFormat, checkWorkingTree,
} from "./tag-release-checks.mjs";
import { CI_WORKFLOW } from "./verify-ci-for-release.mjs";
import { findGoldenPathPass } from "./verify-golden-path-for-release.mjs";

const run = (file, args) => execFileSync(file, args, { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 }).trim();
const git = (...args) => run("git", args);
const gh = (...args) => run("gh", args);

function repositoryName() {
  if (process.env.GITHUB_REPOSITORY) return process.env.GITHUB_REPOSITORY;
  const url = git("remote", "get-url", "origin");
  const match = /github\.com[:/]([^/]+\/[^/]+?)(\.git)?$/.exec(url);
  if (!match) throw new Error("Cannot tell the GitHub repository from the origin address.");
  return match[1];
}

function main() {
  const argv = process.argv.slice(2);
  const version = argv.includes("--version") ? argv[argv.indexOf("--version") + 1] : undefined;
  const refusals = [...checkVersionFormat(version)];
  if (refusals.length) fail(refusals);

  refusals.push(...checkWorkingTree(git("status", "--porcelain", "--untracked-files=no")));
  const head = git("rev-parse", "HEAD^{commit}");
  let originMain = "";
  try { originMain = git("rev-parse", "origin/main^{commit}"); } catch { /* reported by checkPushed */ }
  refusals.push(...checkPushed(head, originMain));
  refusals.push(...checkPropsVersion(readFileSync("Directory.Build.props", "utf8"), version));
  refusals.push(...checkReleaseNotes(readFileSync("RELEASE_NOTES.md", "utf8"), version));
  if (git("tag", "--list", `v${version}`)) refusals.push(`The tag v${version} already exists here.`);
  if (refusals.length) fail(refusals);

  const repository = repositoryName();
  const runs = JSON.parse(gh(
    "run", "list", "--repo", repository, "--workflow", CI_WORKFLOW, "--commit", head, "--limit", "50",
    "--json", "databaseId,event,status,conclusion,headSha",
  )).filter((candidate) => candidate.headSha === head);
  const jobsFor = (candidate) => JSON.parse(gh("run", "view", String(candidate.databaseId), "--repo", repository, "--json", "jobs")).jobs;
  refusals.push(...checkCi(runs, jobsFor));

  const listStatuses = (commit) => gh(
    "api", "--paginate", `repos/${repository}/commits/${commit}/statuses?per_page=100`,
    "--jq", ".[] | {id, context, state, description, created_at}",
  ).split("\n").filter((line) => line.trim()).map((line) => JSON.parse(line));
  // HEAD's own pass, else an earlier commit's with the same shipped files (the release makes the same judgement).
  const golden = findGoldenPathPass(head, { listStatuses, git: (args) => git(...args) });
  refusals.push(...checkGoldenPath(golden));
  if (golden.ok) console.log(describeGoldenPath(golden));
  if (refusals.length) fail(refusals);

  git("tag", "-a", `v${version}`, "-m", `Deluno ${version}`, head);
  git("push", "origin", `v${version}`);
  console.log(`Tagged and pushed v${version} on ${head}. Confirm the Release run is green, the GitHub Release has its assets and the image pulls before calling it released.`);
}

function fail(refusals) {
  console.error("Not tagging:");
  for (const reason of refusals) console.error(`  - ${reason}`);
  process.exit(1);
}

try {
  main();
} catch (error) {
  console.error(`Not tagging: ${error.message}`);
  process.exit(1);
}
