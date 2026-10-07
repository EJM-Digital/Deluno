#!/usr/bin/env node
// The release's safety rule: nothing reaches ghcr or the Releases page unless every check it depends on passed.
// release.yml runs its checks as parallel jobs, so the rule is expressed as the shape of the workflow: two jobs
// publish, one per thing published. `publish-windows` creates the GitHub Release; `publish-docker` pushes the
// image. Each needs the shared checks (ci-passed, golden-path, validate) plus its own build's checks, neither needs
// the other, only they can publish, each holds only the rights for its own thing, and inside each the steps come in a
// safe order. CI's side: ci-passed judges every ci.yml job, and the release's evidence (the ci-passed job and step)
// still exists under its name. docs/ci-standard.md describes the shape.
//
// Usage: node scripts/check-release-workflow-gates.mjs
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { GATES } from "./ci-passed.mjs";
import { REQUIRED_EVIDENCE } from "./verify-ci-for-release.mjs";

const workflowsDir = resolve(dirname(fileURLToPath(import.meta.url)), "..", ".github", "workflows");
const RELEASE = ".github/workflows/release.yml";
const CI = ".github/workflows/ci.yml";

const PUBLISH_WINDOWS = "publish-windows";
const PUBLISH_DOCKER = "publish-docker";
const PUBLISHERS = [PUBLISH_WINDOWS, PUBLISH_DOCKER];

const SHARED_GATES = ["ci-passed", "golden-path", "validate"];
const WINDOWS_NEEDS = [...SHARED_GATES, "windows"];
const DOCKER_NEEDS = [...SHARED_GATES, "docker-candidate"];

// What only the job that creates the GitHub Release may contain, and what only the job that pushes the image may.
const RELEASE_ONLY = ["contents: write", "uses: softprops/action-gh-release@", "gh release"];
const IMAGE_ONLY = ["uses: docker/login-action@", "push: true", "docker push", "imagetools create", "packages: write"];
// Provenance needs the OIDC token and the right to record an attestation, in whichever publish job attests.
const ATTESTATION = ["uses: actions/attest-build-provenance@", "attestations: write", "id-token: write"];

export function loadWorkflows() {
  const read = (name) => readFileSync(resolve(workflowsDir, name), "utf8").replace(/\r\n/g, "\n");
  const names = readdirSync(workflowsDir).filter((file) => /\.ya?ml$/i.test(file));
  return {
    release: read("release.yml"),
    ci: read("ci.yml"),
    others: Object.fromEntries(names.filter((name) => name !== "release.yml").map((name) => [name, read(name)])),
    dockerWorkflowExists: existsSync(resolve(workflowsDir, "docker.yml")),
  };
}

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

// A publish job waits for exactly the shared checks plus its own build's: a missing one lets it publish before that
// check passes, and an extra one lets the other publish job hold it up.
function requireExactNeeds(body, expected, file) {
  const actual = needsOf(body, file);
  for (const job of expected) {
    if (!actual.includes(job)) throw new Error(`${file} does not need ${job}; it could publish before ${job} passes.`);
  }
  for (const job of actual) {
    if (!expected.includes(job)) {
      throw new Error(`${file} needs ${job}, which is not one of its gates (${expected.join(", ")}); the two publish jobs must not wait for each other's build.`);
    }
  }
}

function requireNotOverridden(body, job) {
  const jobIf = (body.match(/\n {4}if: (.*)\n/) || [])[1] || "";
  for (const override of ["always()", "failure()", "cancelled()", "success() ||"]) {
    if (jobIf.includes(override)) throw new Error(`${RELEASE} ${job}'s if: must not contain ${override}; it would run past a failed gate.`);
  }
}

function requireAttestationStep(body, what, job) {
  const steps = body.split(/\n {6}- /).filter((step) => step.includes("uses: actions/attest-build-provenance@"));
  if (steps.length !== 1) throw new Error(`${RELEASE} ${job} must attest ${what}; found ${steps.length} attestation step(s).`);
  requireText(steps[0], "if: ${{ vars.ATTEST_PROVENANCE == 'true' }}", `${RELEASE} ${job} attestation step`);
}

export function checkReleaseGates({ release, ci, others = {}, dockerWorkflowExists = false }) {
  // --- Only release.yml publishes ------------------------------------------------------------------------------

  // Docker used to publish from its own workflow, so an image could go public for a release whose Windows build then
  // failed. release.yml is the only workflow that publishes.
  if (dockerWorkflowExists) {
    throw new Error(".github/workflows/docker.yml must not exist: release.yml builds and publishes the image.");
  }
  for (const [name, text] of Object.entries(others)) {
    for (const marker of ["uses: docker/login-action@", "imagetools create", "uses: softprops/action-gh-release@", "gh release create"]) {
      rejectText(text, marker, `.github/workflows/${name} (only release.yml may publish)`);
    }
  }

  // --- release.yml ---------------------------------------------------------------------------------------------

  // No write access for the workflow as a whole; each publish job grants itself what it needs.
  const releaseTop = release
    .slice(0, release.indexOf("\njobs:\n"))
    .split("\n")
    .filter((line) => !line.trimStart().startsWith("#"))
    .join("\n");
  if (/:\s*write\b|write-all/.test(releaseTop)) {
    throw new Error(`${RELEASE} grants write access at workflow level; only the publish jobs may have it.`);
  }
  requireText(releaseTop, '    tags:\n      - "v*"\n', `${RELEASE} on.push.tags`);
  requireText(releaseTop, "cancel-in-progress: false", `${RELEASE} concurrency (a second push of a tag must never cancel a publish half way)`);

  const releaseJobs = jobIds(release);
  for (const required of [...SHARED_GATES, "windows", "docker-candidate", ...PUBLISHERS]) {
    if (!releaseJobs.includes(required)) throw new Error(`${RELEASE} is missing the ${required} job.`);
  }
  const publishWindows = requireJob(release, PUBLISH_WINDOWS, RELEASE);
  const publishDocker = requireJob(release, PUBLISH_DOCKER, RELEASE);

  // Anything that can put something in public is confined to the publish jobs, and each holds only the rights for
  // its own thing: the Release's write access is not the registry's, nor the reverse.
  for (const job of releaseJobs.filter((name) => !PUBLISHERS.includes(name))) {
    const body = requireJob(release, job, RELEASE);
    for (const marker of [...RELEASE_ONLY, ...IMAGE_ONLY, ...ATTESTATION]) {
      rejectText(body, marker, `${RELEASE} job ${job} (only ${PUBLISHERS.join(" and ")} may publish)`);
    }
  }
  for (const marker of IMAGE_ONLY) {
    rejectText(publishWindows, marker, `${RELEASE} job ${PUBLISH_WINDOWS} (only ${PUBLISH_DOCKER} may touch the registry)`);
  }
  for (const marker of RELEASE_ONLY) {
    rejectText(publishDocker, marker, `${RELEASE} job ${PUBLISH_DOCKER} (only ${PUBLISH_WINDOWS} may publish the GitHub Release)`);
  }

  // Each publish job waits for the shared checks and its own build's checks, and only when they all succeeded.
  requireExactNeeds(publishWindows, WINDOWS_NEEDS, `${RELEASE} ${PUBLISH_WINDOWS}`);
  requireExactNeeds(publishDocker, DOCKER_NEEDS, `${RELEASE} ${PUBLISH_DOCKER}`);
  requireNotOverridden(publishWindows, PUBLISH_WINDOWS);
  requireNotOverridden(publishDocker, PUBLISH_DOCKER);

  // The GitHub Release: the files are validated and attested before the Release is created.
  requireOrder(
    publishWindows,
    ["- name: Download windows artifact", "- name: Validate release assets", "- name: Attest provenance of the Windows release files", "- name: Create GitHub Release"],
    `${RELEASE} ${PUBLISH_WINDOWS} job`,
  );
  for (const marker of ["id-token: write", "attestations: write"]) requireText(publishWindows, marker, `${RELEASE} ${PUBLISH_WINDOWS} job`);
  requireAttestationStep(publishWindows, "the release files", PUBLISH_WINDOWS);

  // The version tag is pushed, attested, checked and smoked before any moving tag moves, and the moving tags move last.
  requireOrder(
    publishDocker,
    [
      "uses: docker/login-action@",
      "- name: Publish release Docker image",
      "push: true",
      "- name: Attest provenance of the container image",
      "- name: Verify pushed digest is addressable",
      "- name: Smoke test published Docker image",
      "- name: Cleanup Docker smoke container",
      "- name: Move the moving image tags",
    ],
    `${RELEASE} ${PUBLISH_DOCKER} job`,
  );
  const movingAt = publishDocker.indexOf("- name: Move the moving image tags");
  if (publishDocker.indexOf("imagetools create") < movingAt) {
    throw new Error(`${RELEASE} ${PUBLISH_DOCKER} may move a tag (imagetools create) only in its last step, after the image passed its smoke.`);
  }
  const movingBody = publishDocker.slice(movingAt);
  if (/\n {6}- /.test(movingBody)) throw new Error(`${RELEASE} ${PUBLISH_DOCKER} must have no step after the moving-tags step.`);
  // A release candidate (a version with a hyphen) leaves the moving step before it creates anything.
  const guardAt = movingBody.indexOf('if [ "$PRERELEASE" = "true" ]');
  const exitAt = movingBody.indexOf("exit 0", guardAt);
  const guardEndAt = movingBody.indexOf("\n          fi\n", guardAt);
  if (guardAt < 0 || exitAt < 0 || guardEndAt < 0 || exitAt > guardEndAt || exitAt > movingBody.indexOf("imagetools create")) {
    throw new Error(`${RELEASE} must leave the moving-tags step for a pre-release before it runs imagetools create.`);
  }
  requireText(movingBody, "PRERELEASE: ${{ steps.version.outputs.prerelease }}", `${RELEASE} moving-tags step`);
  // The image carries its provenance and an SBOM from the build, is attested by digest, and the build reads the cache
  // the candidate wrote.
  for (const marker of [
    "provenance: mode=max",
    "sbom: true",
    "id: build",
    "subject-digest: ${{ steps.build.outputs.digest }}",
    "push-to-registry: true",
    "id-token: write",
    "attestations: write",
    "cache-from: type=gha,scope=deluno-release-amd64",
    "cache-to: type=gha,mode=max,scope=deluno-release-amd64",
  ]) {
    requireText(publishDocker, marker, `${RELEASE} ${PUBLISH_DOCKER} job`);
  }
  requireAttestationStep(publishDocker, "the image", PUBLISH_DOCKER);

  // The tagged commit must be one ci.yml already passed on. The check does not wait for a CI run still going.
  const releaseCiPassed = requireJob(release, "ci-passed", RELEASE);
  for (const marker of ["actions: read", "node scripts/verify-ci-for-release.mjs"]) requireText(releaseCiPassed, marker, `${RELEASE} ci-passed job`);
  rejectText(releaseCiPassed, "--wait", `${RELEASE} ci-passed job (tags are made after CI is green; the gate does not wait)`);

  // ...and one the golden path already passed on: a commit status the person who ran it recorded. The job only reads
  // statuses, so `statuses: read` is all it is granted.
  const goldenPath = requireJob(release, "golden-path", RELEASE);
  for (const marker of ["statuses: read", "node scripts/verify-golden-path-for-release.mjs"]) requireText(goldenPath, marker, `${RELEASE} golden-path job`);
  rejectText(goldenPath, "statuses: write", `${RELEASE} golden-path job (the gate reads the record; it never writes one)`);
  rejectText(goldenPath, "--wait", `${RELEASE} golden-path job (the golden path is run before the tag; the gate does not wait)`);

  // The tag is the version the build carries and has release notes.
  const validate = requireJob(release, "validate", RELEASE);
  for (const marker of ["- name: The tag is the version the build carries", "- name: Release notes have a section for this version"]) {
    requireText(validate, marker, `${RELEASE} validate job`);
  }

  // The amd64 candidate: built unpushed, scanned, started. Its layers go to the cache publish-docker reads.
  const candidate = requireJob(release, "docker-candidate", RELEASE);
  requireOrder(
    candidate,
    ["- name: Build unpushed Docker release candidate", "push: false", "- name: Trivy scan of Docker release candidate", "- name: Start unpushed Docker release candidate"],
    `${RELEASE} docker-candidate job`,
  );
  for (const marker of [
    "cache-from: type=gha,scope=deluno-release-amd64",
    "cache-to: type=gha,mode=max,scope=deluno-release-amd64",
    // The scan must be able to fail the release: a known HIGH or CRITICAL with a fix available blocks it.
    "severity: HIGH,CRITICAL",
    "ignore-unfixed: true",
    'exit-code: "1"',
  ]) {
    requireText(candidate, marker, `${RELEASE} docker-candidate job`);
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
  for (const marker of ["severity: HIGH,CRITICAL", "ignore-unfixed: true", 'exit-code: "1"', "scripts/test-readme-docker-examples.sh"]) {
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
}

export const SUCCESS_MESSAGE =
  "Only publish-windows (the GitHub Release) and publish-docker (the image) can publish, each needs the shared " +
  "checks plus its own build's and neither waits for the other, the moving tags move last and never for a release " +
  "candidate, the image scan can fail the release, the release needs a golden-path status on the tagged commit, " +
  "and ci-passed judges every CI job and still carries the release's evidence.";

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    checkReleaseGates(loadWorkflows());
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
  console.log(SUCCESS_MESSAGE);
}
