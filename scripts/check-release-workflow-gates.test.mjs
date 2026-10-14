// node --test scripts/check-release-workflow-gates.test.mjs
// The release workflow's publish shape, judged on the real workflows and on deliberately broken copies of release.yml.
import assert from "node:assert/strict";
import { test } from "node:test";

import { checkReleaseGates, loadWorkflows } from "./check-release-workflow-gates.mjs";

const real = loadWorkflows();

function replaceOnce(text, from, to) {
  assert.ok(text.includes(from), `the fixture text to replace is not in release.yml: ${from}`);
  return text.replace(from, () => to);
}

const jobBounds = (release, job) => {
  const start = release.indexOf(`\n  ${job}:\n`) + 1;
  assert.ok(start > 0, `release.yml has no job ${job}`);
  const rest = release.slice(start + 1);
  const next = rest.search(/\n {2}[a-zA-Z0-9_-]+:\n/);
  return [start, next < 0 ? release.length : start + 1 + next + 1];
};

const editJob = (release, job, edit) => {
  const [start, end] = jobBounds(release, job);
  return release.slice(0, start) + edit(release.slice(start, end)) + release.slice(end);
};

const withoutNeed = (body, job) =>
  body.replace(/( {4}needs: \[)([^\]]*)\]/, (_, head, names) => {
    const kept = names.split(",").map((name) => name.trim()).filter((name) => name !== job);
    return `${head}${kept.join(", ")}]`;
  });

const rejects = (release, pattern) => assert.throws(() => checkReleaseGates({ ...real, release }), pattern);

test("the real workflows have the intended shape", () => {
  assert.doesNotThrow(() => checkReleaseGates(real));
});

test("publish-windows may not wait for the Docker build", () => {
  rejects(
    editJob(real.release, "publish-windows", (body) => body.replace("windows]", "windows, docker-candidate]")),
    /publish-windows needs docker-candidate/,
  );
});

test("publish-docker may not wait for the Windows build", () => {
  rejects(
    editJob(real.release, "publish-docker", (body) => body.replace("docker-candidate]", "docker-candidate, windows]")),
    /publish-docker needs windows/,
  );
});

test("neither publish job may wait for the other", () => {
  rejects(
    editJob(real.release, "publish-windows", (body) => body.replace("windows]", "windows, publish-docker]")),
    /publish-windows needs publish-docker/,
  );
  rejects(
    editJob(real.release, "publish-docker", (body) => body.replace("docker-candidate]", "docker-candidate, publish-windows]")),
    /publish-docker needs publish-windows/,
  );
});

test("each publish job needs every shared check and its own build's checks", () => {
  for (const job of ["ci-passed", "golden-path", "validate", "windows"]) {
    rejects(editJob(real.release, "publish-windows", (body) => withoutNeed(body, job)), new RegExp(`publish-windows does not need ${job}`));
  }
  for (const job of ["ci-passed", "golden-path", "validate", "docker-candidate"]) {
    rejects(editJob(real.release, "publish-docker", (body) => withoutNeed(body, job)), new RegExp(`publish-docker does not need ${job}`));
  }
});

test("a publish job may not run past a failed gate", () => {
  for (const job of ["publish-windows", "publish-docker"]) {
    rejects(
      editJob(real.release, job, (body) => body.replace("    needs:", "    if: ${{ always() }}\n    needs:")),
      new RegExp(`${job}'s if: must not contain always\\(\\)`),
    );
  }
});

test("a third job may not publish", () => {
  const third = (permissions) =>
    `${real.release}\n  publish-extra:\n    runs-on: ubuntu-latest\n    timeout-minutes: 10\n    permissions:\n      ${permissions}\n    steps:\n      - run: echo hi\n`;
  for (const permissions of ["packages: write", "contents: write", "id-token: write", "attestations: write"]) {
    rejects(third(permissions), /only publish-windows and publish-docker may publish/);
  }
});

test("a build job may not log in to the registry, push, release or move a tag", () => {
  for (const [job, marker] of [
    ["docker-candidate", "      - uses: docker/login-action@dbcb813823bdd20940b903addbd779551569679f # v4.6.0\n"],
    ["docker-candidate", "      - run: docker push example\n"],
    ["windows", "      - run: gh release create v1\n"],
    ["validate", "      - run: docker buildx imagetools create --tag a:latest b\n"],
  ]) {
    rejects(
      editJob(real.release, job, (body) => body.replace("    steps:\n", `    steps:\n${marker}`)),
      new RegExp(`job ${job} \\(only publish-windows and publish-docker may publish\\)`),
    );
  }
});

test("publish-windows holds no registry rights and publish-docker no Release rights", () => {
  rejects(editJob(real.release, "publish-windows", (body) => body.replace("      contents: write\n", "      contents: write\n      packages: write\n")), /packages: write/);
  rejects(editJob(real.release, "publish-docker", (body) => body.replace("      contents: read\n", "      contents: write\n")), /contents: write/);
  rejects(editJob(real.release, "publish-windows", (body) => body.replace("    steps:\n", "    steps:\n      - run: echo push: true\n")), /push: true/);
});

test("workflow-level write access is refused", () => {
  rejects(replaceOnce(real.release, "permissions:\n  contents: read\n", "permissions:\n  contents: write\n"), /write access at workflow level/);
});

test("another workflow may not publish, and docker.yml may not come back", () => {
  assert.throws(
    () => checkReleaseGates({ ...real, others: { ...real.others, "x.yml": "      - uses: docker/login-action@abc\n" } }),
    /only release.yml may publish/,
  );
  assert.throws(() => checkReleaseGates({ ...real, dockerWorkflowExists: true }), /docker.yml must not exist/);
});

test("publish-docker's steps keep their order", () => {
  const swapped = editJob(real.release, "publish-docker", (body) =>
    body
      .replace("- name: Verify pushed digest is addressable", "- name: SWAP")
      .replace("- name: Smoke test published Docker image", "- name: Verify pushed digest is addressable")
      .replace("- name: SWAP", "- name: Smoke test published Docker image"),
  );
  rejects(swapped, /unsafe release gate order/);
});

test("the moving tags move in the last step of publish-docker", () => {
  rejects(
    editJob(real.release, "publish-docker", (body) => `${body.trimEnd()}\n\n      - name: After\n        run: echo done\n`),
    /no step after the moving-tags step/,
  );
  rejects(
    editJob(real.release, "publish-docker", (body) =>
      replaceOnce(body, "      - name: Publish release Docker image", "      - run: docker buildx imagetools create --tag x:latest y\n\n      - name: Publish release Docker image"),
    ),
    /only in its last step/,
  );
});

test("a release candidate moves no moving tag", () => {
  rejects(
    editJob(real.release, "publish-docker", (body) => body.replace('if [ "$PRERELEASE" = "true" ]', 'if [ "$PRERELEASE" = "never" ]')),
    /leave the moving-tags step for a pre-release/,
  );
  rejects(
    editJob(real.release, "publish-docker", (body) => body.replace('moving tags."\n            exit 0\n', 'moving tags."\n')),
    /leave the moving-tags step for a pre-release/,
  );
});

test("publish-docker's build reads and writes the shared cache", () => {
  rejects(editJob(real.release, "publish-docker", (body) => body.replace("          cache-from: type=gha,scope=deluno-release-amd64\n", "")), /cache-from/);
  rejects(editJob(real.release, "docker-candidate", (body) => body.replace("          cache-to: type=gha,mode=max,scope=deluno-release-amd64,ignore-error=true\n", "")), /cache-to/);
});

test("publish-windows attests the release files only when ATTEST_PROVENANCE is true, before the Release", () => {
  rejects(editJob(real.release, "publish-windows", (body) => body.replace("if: ${{ vars.ATTEST_PROVENANCE == 'true' }}\n", "")), /ATTEST_PROVENANCE/);
  const swapped = editJob(real.release, "publish-windows", (body) =>
    body
      .replace("- name: Attest provenance of the Windows release files", "- name: SWAP")
      .replace("- name: Create GitHub Release", "- name: Attest provenance of the Windows release files")
      .replace("- name: SWAP", "- name: Create GitHub Release"),
  );
  rejects(swapped, /unsafe release gate order/);
});

test("publish-docker attests the pushed image by digest, only when ATTEST_PROVENANCE is true", () => {
  rejects(editJob(real.release, "publish-docker", (body) => body.replace("if: ${{ vars.ATTEST_PROVENANCE == 'true' }}\n", "")), /ATTEST_PROVENANCE/);
  rejects(editJob(real.release, "publish-docker", (body) => body.replace("subject-digest: ${{ steps.build.outputs.digest }}", "subject-digest: sha256:x")), /subject-digest/);
});

test("the Docker candidate stays unpushed, and its scan can fail the release", () => {
  rejects(editJob(real.release, "docker-candidate", (body) => body.replace("push: false", "push: true")), /push: true/);
  rejects(editJob(real.release, "docker-candidate", (body) => body.replace('exit-code: "1"', 'exit-code: "0"')), /exit-code/);
});

test("ci-passed must judge every CI job", () => {
  // Edit ci-passed's own list: another job's `needs: [changes, ...]` may come first in the file.
  const ci = real.ci.replace(/(\n  ci-passed:\r?\n    needs: \[changes, )(\w[\w-]*, )/, "$1");
  assert.notEqual(ci, real.ci, "the fixture edit must change ci.yml");
  assert.throws(() => checkReleaseGates({ ...real, ci }), /ci-passed does not need/);
});

test("the golden-path job is required and only reads statuses", () => {
  rejects(editJob(real.release, "golden-path", (body) => body.replace("verify-golden-path-for-release.mjs", "something-else.mjs")), /golden-path job/);
  rejects(editJob(real.release, "golden-path", (body) => body.replace("statuses: read", "statuses: write")), /statuses/);
  rejects(editJob(real.release, "golden-path", (body) => body.replace("run: node scripts/verify-golden-path-for-release.mjs", "run: node scripts/verify-golden-path-for-release.mjs --wait")), /--wait/);
});

test("the validate job keeps its two checks", () => {
  rejects(editJob(real.release, "validate", (body) => body.replace("- name: Release notes have a section for this version", "- name: Skipped")), /validate job/);
});
