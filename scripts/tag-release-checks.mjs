// The checks scripts/tag-release.ps1 makes before it creates and pushes a release tag. Kept apart from the git and
// GitHub calls so each refusal can be tested on plain values (scripts/tag-release.test.mjs). The CI and golden-path
// judgements are the release workflow's own (verify-ci-for-release.mjs, verify-golden-path-for-release.mjs), so the
// script and the release can never disagree about what counts.
import { evaluateRun, ACCEPTED_EVENTS } from "./verify-ci-for-release.mjs";

export const VERSION_PATTERN = /^\d+\.\d+\.\d+(-rc\.\d+(\.\d+)?)?$/;

// A run that only skipped jobs (a change to documents alone) is green without having tested anything, so a tag also
// needs the browser smoke shards, which every code change runs, to have passed.
export const SMOKE_JOBS = ["Frontend smoke (1/2)", "Frontend smoke (2/2)"];

export function checkVersionFormat(version) {
  return VERSION_PATTERN.test(version || "")
    ? []
    : [`"${version}" is not a release version. Use x.y.z, x.y.z-rc.N or x.y.z-rc.N.M (for example 1.0.0-rc.30.15).`];
}

export function checkWorkingTree(statusOutput) {
  return statusOutput.trim()
    ? ["The working tree has changes that are not committed. Commit or remove them first."]
    : [];
}

export function checkPushed(headSha, originMainSha) {
  if (!originMainSha) return ["origin/main is not known here. Run git fetch origin first."];
  return headSha === originMainSha
    ? []
    : ["HEAD is not the same commit as origin/main. Push your commits (or pull), and tag only what is on origin/main."];
}

export function checkPropsVersion(propsText, version) {
  const match = /<Version>\s*([^<\s]+)\s*<\/Version>/.exec(propsText);
  if (!match) return ["Directory.Build.props has no <Version>."];
  return match[1] === version
    ? []
    : [`Directory.Build.props says version ${match[1]}, not ${version}. Change it, commit and push, then tag.`];
}

export function checkReleaseNotes(notesText, version) {
  const heading = `## ${version}`;
  const found = notesText.split(/\r?\n/).some((line) => line.trim() === heading);
  return found ? [] : [`RELEASE_NOTES.md has no "${heading}" section. Write the release notes, commit and push, then tag.`];
}

// runs: ci.yml runs for HEAD (databaseId, event, status, conclusion). jobsFor(run) returns that run's jobs.
export function checkCi(runs, jobsFor) {
  const considered = runs.filter((run) => ACCEPTED_EVENTS.includes(run.event));
  if (considered.length === 0) {
    return ["No push or manual run of ci.yml exists for HEAD. Push it and wait for CI to finish, or start one with gh workflow run ci.yml."];
  }
  const reasons = [];
  for (const run of considered) {
    const jobs = run.status === "completed" && run.conclusion === "success" ? jobsFor(run) : [];
    const problems = evaluateRun(run, jobs, run.conclusion === "success" ? undefined : []);
    if (problems.length === 0) {
      const missing = SMOKE_JOBS.filter((name) => !jobs.some((job) => job.name === name && job.conclusion === "success"));
      if (missing.length === 0) return [];
      reasons.push(`run ${run.databaseId} passed but did not run the jobs (${missing.join(", ")} did not pass): it was probably a documents-only change. Start a full run with gh workflow run ci.yml.`);
    } else {
      reasons.push(`run ${run.databaseId}: ${problems.join("; ")}`);
    }
  }
  return [`CI has not passed on HEAD. ${reasons.join(" | ")}`];
}

// outcome: what findGoldenPathPass (verify-golden-path-for-release.mjs) returned for HEAD: the commit's own pass, or
// an earlier commit's with the same shipped files (scripts/ship-paths.mjs), the same judgement the release makes.
export function checkGoldenPath(outcome) {
  return outcome.ok ? [] : [`The golden path has not passed on HEAD: ${outcome.problems.join("; ")}. See docs/golden-path.md.`];
}

export function describeGoldenPath(outcome) {
  return outcome.carried
    ? `The golden path passed on ${outcome.sha}, an earlier commit, and the shipped files are identical to HEAD (${outcome.reason}).`
    : "The golden path passed on HEAD.";
}
