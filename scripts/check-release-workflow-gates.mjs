#!/usr/bin/env node
// The release's safety rule: nothing reaches ghcr or the Releases page unless every check passed.
// release.yml runs its checks as parallel jobs, so the rule is expressed as the shape of the workflow: one
// `publish` job needs every other job and is the only one able to publish anything, and inside publish the steps
// come in a safe order. CI's side: ci-passed judges every ci.yml job, and the release's evidence (the ci-passed
// job and step) still exists under its name.
//
// Usage: node scripts/check-release-workflow-gates.mjs
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { GATES } from "./ci-passed.mjs";
import { REQUIRED_EVIDENCE } from "./verify-ci-for-release.mjs";

const workflowsDir = resolve(dirname(fileURLToPath(import.meta.url)), "..", ".github", "workflows");
const read = (name) => readFileSync(resolve(workflowsDir, name), "utf8").replace(/\r\n/g, "\n");
const release = read("release.yml");
const ci = read("ci.yml");
const RELEASE = ".github/workflows/release.yml";
const CI = ".github/workflows/ci.yml";

function requireText(source, marker, file) {
  const index = source.indexOf(marker);
  if (index < 0) throw new Error(`${file} is missing required release gate marker: ${marker}`);
  return index;
}

function requireOrder(source, markers, file) {
  let previous = -1;
  for (const marker of markers) {
    const current = requireText(source, marker, file);
    if (current <= previous) throw new Error(`${file} has an unsafe release gate order near: ${marker}`);
    previous = current;
  }
}

function jobIds(source) {
  const jobsStart = source.indexOf("\njobs:\n");
  if (jobsStart < 0) throw new Error("A workflow has no jobs: section.");
  return [...source.slice(jobsStart).matchAll(/\n {2}([a-zA-Z0-9_-]+):\n/g)].map((match) => match[1]);
}

function requireJob(source, jobName, file) {
  const marker = `\n  ${jobName}:\n`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`${file} is missing required job: ${jobName}`);
  const remaining = source.slice(start + marker.length);
  const nextJob = remaining.search(/\n {2}[a-zA-Z0-9_-]+:\n/);
  return nextJob < 0 ? remaining : remaining.slice(0, nextJob);
}

function rejectText(source, marker, file) {
  if (source.includes(marker)) throw new Error(`${file} contains forbidden workflow text: ${marker}`);
}

function needsOf(body, file) {
  const line = `\n${body}`.match(/\n {4}needs: \[([^\]]*)\]\n/);
  if (!line) throw new Error(`${file} must declare its needs as needs: [job, ...] on one line.`);
  return line[1].split(",").map((name) => name.trim()).filter(Boolean);
}

// --- One publisher ---------------------------------------------------------------------------------------------

// Docker used to publish from its own workflow, so an image could go public for a release whose Windows build then
// failed. release.yml is the only workflow that publishes.
if (existsSync(resolve(workflowsDir, "docker.yml"))) {
  throw new Error(".github/workflows/docker.yml must not exist: release.yml builds and publishes the image together with the Windows build.");
}
for (const name of readdirSync(workflowsDir).filter((file) => /\.ya?ml$/i.test(file) && file !== "release.yml")) {
  const text = read(name);
  for (const marker of ["uses: docker/login-action@", "imagetools create", "uses: softprops/action-gh-release@", "gh release create"]) {
    rejectText(text, marker, `.github/workflows/${name} (only release.yml may publish)`);
  }
}

// --- release.yml -----------------------------------------------------------------------------------------------

const releaseTop = release
  .slice(0, release.indexOf("\njobs:\n"))
  .split("\n")
  .filter((line) => !line.trimStart().startsWith("#"))
  .join("\n");
if (/:\s*write\b|write-all/.test(releaseTop)) {
  throw new Error(`${RELEASE} grants write access at workflow level; only the publish job may have it.`);
}
requireText(releaseTop, '    tags:\n      - "v*"\n', `${RELEASE} on.push.tags`);
requireText(releaseTop, "cancel-in-progress: false", `${RELEASE} concurrency (a second push of a tag must never cancel a publish half way)`);

const releaseJobs = jobIds(release);
for (const required of ["ci-passed", "windows", "docker", "publish"]) {
  if (!releaseJobs.includes(required)) throw new Error(`${RELEASE} is missing the ${required} job.`);
}
const publish = requireJob(release, "publish", RELEASE);

// Anything that can put something in public is confined to publish.
const publishingText = [
  "uses: docker/login-action@",
  "push: true",
  "docker push",
  "imagetools create",
  "packages: write",
  "contents: write",
  "attestations: write",
  "uses: softprops/action-gh-release@",
  "uses: actions/attest-build-provenance@",
  "gh release",
];
for (const job of releaseJobs.filter((name) => name !== "publish")) {
  const body = requireJob(release, job, RELEASE);
  for (const marker of publishingText) rejectText(body, marker, `${RELEASE} job ${job} (only publish may publish)`);
}

// publish waits for every other job, and only when they all succeeded.
const publishNeeds = needsOf(publish, `${RELEASE} publish`);
for (const job of releaseJobs.filter((name) => name !== "publish")) {
  if (!publishNeeds.includes(job)) throw new Error(`${RELEASE} publish does not need ${job}; it could publish before ${job} passes.`);
}
const publishIf = (publish.match(/\n {4}if: (.*)\n/) || [])[1] || "";
for (const override of ["always()", "failure()", "cancelled()", "success() ||"]) {
  if (publishIf.includes(override)) throw new Error(`${RELEASE} publish's if: must not contain ${override}; it would run past a failed gate.`);
}

// The version tag is pushed, checked and smoked, and the release published, before any moving tag moves.
requireOrder(
  publish,
  [
    "uses: docker/login-action@",
    "- name: Publish release Docker image",
    "push: true",
    "- name: Verify pushed digest is addressable",
    "- name: Smoke test published Docker image",
    "- name: Create GitHub Release",
    "- name: Move the moving image tags",
  ],
  `${RELEASE} publish job`,
);
const movingAt = publish.indexOf("imagetools create");
if (movingAt < 0 || movingAt < publish.indexOf("- name: Move the moving image tags")) {
  throw new Error(`${RELEASE} publish may move a tag (imagetools create) only in its last step, after the GitHub Release exists.`);
}
// Provenance and an SBOM go out with the image.
for (const marker of ["provenance: mode=max", "sbom: true"]) requireText(publish, marker, `${RELEASE} publish job`);

// The tagged commit must be one ci.yml already passed on. The check does not wait for a CI run still going.
const releaseCiPassed = requireJob(release, "ci-passed", RELEASE);
for (const marker of ["actions: read", "node scripts/verify-ci-for-release.mjs"]) requireText(releaseCiPassed, marker, `${RELEASE} ci-passed job`);
rejectText(releaseCiPassed, "--wait", `${RELEASE} ci-passed job (tags are made after CI is green; the gate does not wait)`);

// The amd64 candidate: built unpushed, scanned, started.
requireOrder(
  requireJob(release, "docker", RELEASE),
  ["- name: Build unpushed Docker release candidate", "push: false", "- name: Trivy scan of Docker release candidate", "- name: Start unpushed Docker release candidate"],
  `${RELEASE} docker job`,
);
for (const marker of ["severity: CRITICAL", "ignore-unfixed: true", 'exit-code: "1"']) {
  requireText(requireJob(release, "docker", RELEASE), marker, `${RELEASE} docker job (the scan must be able to fail the release)`);
}

// Checksums describe the files as published. Signing rewrites Setup.exe, so hashing must come after it.
requireOrder(
  requireJob(release, "windows", RELEASE),
  ["- name: Sign installer outputs", "- name: Create portable zip and hashes", "- name: Upload Velopack artifacts"],
  `${RELEASE} windows job`,
);

// --- ci.yml ----------------------------------------------------------------------------------------------------

// ci-passed judges every other ci.yml job, always runs, and knows each job's flag.
const ciJobIds = jobIds(ci);
const verdictJob = requireJob(ci, "ci-passed", CI);
for (const marker of ["if: ${{ always() }}", "NEEDS: ${{ toJSON(needs) }}", "node scripts/ci-passed.mjs"]) {
  requireText(verdictJob, marker, `${CI} ci-passed job`);
}
const verdictNeeds = needsOf(verdictJob, `${CI} ci-passed`);
for (const job of ciJobIds.filter((name) => name !== "ci-passed")) {
  if (!verdictNeeds.includes(job)) throw new Error(`${CI} ci-passed does not need ${job}, so CI could pass without it.`);
}
const judged = ciJobIds.filter((name) => !["changes", "ci-passed"].includes(name)).sort();
if (JSON.stringify(judged) !== JSON.stringify(Object.keys(GATES).sort())) {
  throw new Error(`scripts/ci-passed.mjs GATES (${Object.keys(GATES).sort().join(", ")}) must list exactly the ci.yml jobs it judges (${judged.join(", ")}).`);
}
// Every flag a GATES entry names is published by changes.
const changesJob = requireJob(ci, "changes", CI);
for (const flag of new Set(Object.values(GATES).filter(Boolean))) {
  requireText(changesJob, `      ${flag}: \${{ steps.flags.outputs.${flag} }}`, `${CI} changes job outputs`);
}
// The image scan in CI can fail the build.
const ciDocker = requireJob(ci, "docker", CI);
for (const marker of ["severity: CRITICAL", "ignore-unfixed: true", 'exit-code: "1"', "scripts/test-readme-docker-examples.sh"]) {
  requireText(ciDocker, marker, `${CI} docker job`);
}

// Every job and step the release gate asks CI for must still exist under that name.
function ciJobBody(displayName) {
  for (const id of ciJobIds) {
    const body = requireJob(ci, id, CI);
    const named = body.match(/^ {4}name: (.*)$/m);
    if ((named ? named[1] : id) === displayName) return body;
  }
  throw new Error(`${CI} has no job named "${displayName}", which scripts/verify-ci-for-release.mjs requires. Update one to match the other.`);
}
for (const want of REQUIRED_EVIDENCE) {
  const body = ciJobBody(want.job);
  for (const step of want.steps) requireText(body, `- name: ${step}\n`, `${CI} job "${want.job}"`);
}

console.log(
  "Every release check gates publish, only publish can publish, the moving tags move last, the image scan can " +
    "fail the release, and ci-passed judges every CI job and still carries the release's evidence.",
);
