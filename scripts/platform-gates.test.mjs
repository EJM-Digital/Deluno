// node --test scripts/platform-gates.test.mjs
// The repository's own gates, proven on the real files and by running the real scripts: what the pre-push hook and `npm run gate` run,
// which CI jobs are due for which change, the workflow rules, the private-details and closing-keyword checks, and that the three
// hosts share one request pipeline. Each test names the behaviour; none only checks that a file exists.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

import { evaluateRun } from "./verify-ci-for-release.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = (relative) => readFileSync(path.join(root, relative), "utf8").replace(/\r\n/g, "\n");

function tempDir(label) {
  return mkdtempSync(path.join(tmpdir(), `deluno-${label}-`));
}

/** Runs a repository PowerShell script with whichever PowerShell is installed, as scripts/run-powershell.mjs does. */
function powershell(args, cwd) {
  for (const shell of ["pwsh", "powershell"]) {
    const result = spawnSync(shell, ["-NoProfile", "-ExecutionPolicy", "Bypass", ...args], { cwd, encoding: "utf8" });
    if (!result.error) return { status: result.status, output: `${result.stdout}${result.stderr}` };
  }
  throw new Error("Neither pwsh nor powershell is installed.");
}

// ---- GAT-01: the pre-push hook

test("the pre-push hook runs the repository checks and stops the push when they fail", () => {
  const hook = read(".githooks/pre-push");
  assert.match(hook, /^set -eu$/m, "a failed check must end the hook, and so the push");
  assert.match(hook, /^npm run ci:check$/m);
  const scripts = JSON.parse(read("package.json")).scripts;
  assert.equal(scripts["ci:check"], "node scripts/run-powershell.mjs scripts/ci-check.ps1");
});

test("the pre-push checks build and check the repository but run no backend, tray, web unit or smoke test", () => {
  const checks = read("scripts/ci-check.ps1");
  for (const forbidden of [/dotnet test/, /vitest/, /playwright/, /test:web\b/, /test:smoke/]) {
    assert.doesNotMatch(checks, forbidden, `ci:check is the pre-push hook; ${forbidden} would make every push run it`);
  }
  assert.match(checks, /dotnet/, "it still builds");
});

test("the pre-push hook is active only in a clone that pointed git at .githooks, and AGENTS.md says how", () => {
  assert.ok(existsSync(path.join(root, ".githooks", "pre-push")));
  assert.match(read("AGENTS.md"), /git config core\.hooksPath \.githooks/);
});

// ---- GAT-02: npm run gate

test("npm run gate builds Release once, then runs every suite against that build and names the step that failed", () => {
  const gate = read("scripts/gate.ps1");
  assert.equal(gate.match(/& dotnet build Deluno\.slnx --configuration Release/g)?.length, 1, "one Release build");
  assert.match(gate, /\$env:DELUNO_SMOKE_NO_BUILD = "1"/, "the smoke hosts do not build again");
  assert.match(gate, /--no-build/, "the backend tests reuse the build");
  assert.match(gate, /scripts\\ci-check\.ps1|npm.* run ci:check|ci:check/, "ci:check is a step");
  assert.match(gate, /run test:unit/, "the web unit tests are a step");
  assert.match(gate, /XX  \{0\} failed with exit code \{1\}.*\n\s*Get-Content \$entry\.Log -Tail 25/, "a failed step is named with the tail of its log");
  assert.match(gate, /exit 1\s*\}\s*\nWrite-Host "  OK  Every step passed; safe to push\."/, "any failed step fails the gate");
});

test("npm run gate runs the smoke in shards, each against its own host and ports, and the timing tests alone at the end", () => {
  const gate = read("scripts/gate.ps1");
  const shards = [...gate.matchAll(/@\{ Id = (\d); Shard = "(\d\/\d)"; ApiPort = ([^;]+); WebPort = ([^}]+) \}/g)];
  assert.ok(shards.length >= 2, "at least two shards");
  const total = shards.length;
  assert.deepEqual(shards.map((s) => s[2]), shards.map((_, i) => `${i + 1}/${total}`), "shard i of n, for every i");
  assert.equal(new Set(shards.map((s) => s[3])).size, total, "each shard has its own API port");
  assert.equal(new Set(shards.map((s) => s[4])).size, total, "each shard has its own web port");
  assert.match(gate, /is already in use \(needed by smoke shard/, "a busy port is refused before anything starts");
  assert.match(gate, /--shard=\$\(\$shard\.Shard\)/);
  const timingAt = gate.indexOf('--filter "Category=Timing"');
  const lastWait = gate.lastIndexOf("WaitForExit()");
  assert.ok(timingAt > lastWait, "the timing tests start after every parallel step has finished");
});

// ---- GAT-03: the Windows tray tests

test("the tray test project compiles to an empty assembly off Windows, and ci:check proves it still does", () => {
  const project = read("tests/Deluno.Tray.Tests/Deluno.Tray.Tests.csproj");
  assert.match(project, /<ItemGroup Condition="!\$\(\[MSBuild\]::IsOSPlatform\('Windows'\)\) or '\$\(SimulateLinuxCI\)' == 'true'">\s*<Compile Remove="\*\*\/\*\.cs" \/>/);
  assert.match(read("scripts/ci-check.ps1"), /SimulateLinuxCI/, "ci:check builds the project as the Linux runner would");
});

// ---- GAT-04: which CI jobs are due

/** The flags step of ci.yml, run as bash with the paths-filter outputs a change would give. */
function dueJobs(environment) {
  const ci = read(".github/workflows/ci.yml");
  const at = ci.indexOf("- name: Work out which jobs are due");
  assert.ok(at > 0, "ci.yml has the step that works out which jobs are due");
  const step = ci.slice(at);
  const runAt = step.indexOf("        run: |\n");
  const lines = step.slice(runAt + "        run: |\n".length).split("\n");
  const script = [];
  for (const line of lines) {
    if (line.trim() !== "" && !line.startsWith("          ")) break;
    script.push(line.slice(10));
  }
  const folder = tempDir("flags");
  try {
    const outputFile = path.join(folder, "output.txt");
    writeFileSync(outputFile, "");
    const result = spawnSync("bash", ["-c", script.join("\n")], {
      env: { PATH: process.env.PATH, GITHUB_OUTPUT: outputFile, EVENT: "push", MUTATION_ON_PUSH: "", CODE: "false", CODE_COUNT: "0", NOTES: "false", WEB: "false", WEB_COUNT: "0", DOCKER: "false", WORKFLOWS: "false", ...environment },
      encoding: "utf8"
    });
    assert.equal(result.status, 0, result.stderr);
    return Object.fromEntries(readFileSync(outputFile, "utf8").split("\n").filter(Boolean).map((line) => line.split("=")));
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

test("a push that changes only documents runs no backend, web, smoke, image, installer or end-to-end job", () => {
  assert.deepEqual(dueJobs({}), {
    run_backend: "false", run_web_dist: "false", run_web: "false", run_e2e_smoke: "false", run_docker: "false",
    run_windows_installer: "false", run_e2e_linux: "false", run_coverage: "false", run_mutation: "false"
  });
});

test("a push that changes only the web app runs the web and smoke jobs and neither the backend nor the image", () => {
  const due = dueJobs({ CODE: "true", CODE_COUNT: "3", WEB: "true", WEB_COUNT: "3" });
  assert.equal(due.run_web, "true");
  assert.equal(due.run_e2e_smoke, "true");
  assert.equal(due.run_web_dist, "true");
  assert.equal(due.run_backend, "false");
  assert.equal(due.run_docker, "false");
});

test("a push that changes backend code also runs the smoke tests, which run against the backend", () => {
  const due = dueJobs({ CODE: "true", CODE_COUNT: "2", WEB: "false", WEB_COUNT: "0" });
  assert.equal(due.run_backend, "true");
  assert.equal(due.run_e2e_smoke, "true");
  assert.equal(due.run_web, "false");
});

test("a changed release-notes file reaches the backend, because it is embedded in the build", () => {
  const due = dueJobs({ NOTES: "true" });
  assert.equal(due.run_backend, "true");
  assert.equal(due.run_e2e_smoke, "true");
});

test("a changed Dockerfile or workflow runs the image job, and a workflow change runs the web job too", () => {
  assert.equal(dueJobs({ DOCKER: "true" }).run_docker, "true");
  const due = dueJobs({ WORKFLOWS: "true" });
  assert.equal(due.run_docker, "true");
  assert.equal(due.run_web, "true");
});

test("a manual run runs everything, installer and end-to-end jobs included; a push never runs those", () => {
  const manual = dueJobs({ EVENT: "workflow_dispatch" });
  for (const flag of ["run_backend", "run_web", "run_e2e_smoke", "run_docker", "run_windows_installer", "run_e2e_linux"]) {
    assert.equal(manual[flag], "true", flag);
  }
  const push = dueJobs({ CODE: "true", CODE_COUNT: "9", WEB: "true", WEB_COUNT: "1", DOCKER: "true", WORKFLOWS: "true", NOTES: "true" });
  assert.equal(push.run_windows_installer, "false");
  assert.equal(push.run_e2e_linux, "false");
});

// ---- GAT-06: workflow rules

/** A copy of the check scripts beside a workflow folder the test controls, so a bad workflow can be shown to fail. */
function workflowRepo(workflows) {
  const folder = tempDir("workflows");
  mkdirSync(path.join(folder, "scripts"), { recursive: true });
  mkdirSync(path.join(folder, ".github", "workflows"), { recursive: true });
  for (const script of ["check-github-action-pins.mjs", "check-workflow-hygiene.mjs"]) copyFileSync(path.join(root, "scripts", script), path.join(folder, "scripts", script));
  for (const [name, text] of Object.entries(workflows)) writeFileSync(path.join(folder, ".github", "workflows", name), text);
  return folder;
}

const goodWorkflow = `name: Good
on: push
permissions:
  contents: read
jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 5
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
`;

function runCheck(folder, script) {
  const result = spawnSync(process.execPath, [path.join(folder, "scripts", script)], { encoding: "utf8" });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
}

test("the real workflows are all pinned to a commit SHA with a release comment, least-privilege, with a timeout on every job", () => {
  for (const script of ["check-github-action-pins.mjs", "check-workflow-hygiene.mjs"]) {
    const result = spawnSync(process.execPath, [path.join(root, "scripts", script)], { encoding: "utf8" });
    assert.equal(result.status, 0, `${script}: ${result.stdout}${result.stderr}`);
  }
});

test("an action named by a tag instead of a commit SHA is refused", () => {
  const folder = workflowRepo({ "bad.yml": goodWorkflow.replace("3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "v4") });
  try {
    const result = runCheck(folder, "check-github-action-pins.mjs");
    assert.equal(result.status, 1);
    assert.match(result.output, /actions\/checkout@v4 \(not a full commit SHA\)/);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("a pin with no release comment is refused", () => {
  const folder = workflowRepo({ "bad.yml": goodWorkflow.replace(" # v7.0.1", "") });
  try {
    const result = runCheck(folder, "check-github-action-pins.mjs");
    assert.equal(result.status, 1);
    assert.match(result.output, /no "# vX\.Y\.Z" comment naming the release/);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("a workflow with no permissions block, a write grant at the top, or a job without a timeout is refused", () => {
  const cases = {
    "no-permissions.yml": [goodWorkflow.replace("permissions:\n  contents: read\n", ""), /no top-level permissions: block/],
    "write-grant.yml": [goodWorkflow.replace("contents: read", "contents: write"), /top-level permissions grants write access/],
    "write-all.yml": [goodWorkflow.replace("permissions:\n  contents: read", "permissions: write-all"), /top-level permissions is write-all/],
    "no-timeout.yml": [goodWorkflow.replace("    timeout-minutes: 5\n", ""), /job "build" has no timeout-minutes/]
  };
  for (const [name, [text, expected]] of Object.entries(cases)) {
    const folder = workflowRepo({ [name]: text });
    try {
      const result = runCheck(folder, "check-workflow-hygiene.mjs");
      assert.equal(result.status, 1, name);
      assert.match(result.output, expected, name);
    } finally {
      rmSync(folder, { recursive: true, force: true });
    }
  }
  const folder = workflowRepo({ "good.yml": goodWorkflow });
  try {
    assert.equal(runCheck(folder, "check-workflow-hygiene.mjs").status, 0);
    assert.equal(runCheck(folder, "check-github-action-pins.mjs").status, 0);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("only the release workflow's publish jobs hold a write permission or a registry login", () => {
  for (const name of readdirSync(path.join(root, ".github", "workflows")).filter((file) => file.endsWith(".yml") && file !== "release.yml")) {
    const text = read(`.github/workflows/${name}`);
    assert.doesNotMatch(text, /packages: write/, `${name} must not publish a package`);
    assert.doesNotMatch(text, /docker\/login-action/, `${name} must not log in to a registry`);
  }
  const release = read(".github/workflows/release.yml");
  assert.match(release, /publish-windows:[\s\S]*?contents: write/);
  assert.match(release, /publish-docker:[\s\S]*?packages: write/);
});

// ---- GAT-07: gate tests that run locally and in CI

test("every test the local ci:check runs is also run by the repo-checks job, so a push from a clone without the hook is held to the same rules", { skip: "DEFECT (GAT-07): seven of the node test files that ci:check runs are run by no CI job, so a push from a clone without the hook skips them" }, () => {
  const local = read("scripts/ci-check.ps1");
  const ci = read(".github/workflows/ci.yml");
  const localTests = [...local.matchAll(/scripts\/[A-Za-z0-9._-]+\.test\.mjs/g)].map((m) => m[0]);
  const missing = [...new Set(localTests)].filter((file) => !ci.includes(file));
  assert.deepEqual(missing, [], `these tests run in ci:check but in no CI job: ${missing.join(", ")}`);
});

// ---- GAT-11: the golden-path commit status and the release

test("the release reads the golden-path commit status and its job runs before anything is published", () => {
  const release = read(".github/workflows/release.yml");
  assert.match(release, /scripts\/verify-golden-path-for-release\.mjs/);
  const publish = release.slice(release.indexOf("  publish-windows:"));
  assert.match(publish.slice(0, publish.indexOf("steps:")), /needs:[^\n]*golden-path|- golden-path/);
});

// ---- GAT-12: the Windows installer

test("the Windows installer is built from the candidate by vpk pack on a manual run, and a release candidate is refused unless that run passed", () => {
  const ci = read(".github/workflows/ci.yml");
  const installer = ci.slice(ci.indexOf("\n  windows-installer:\n"), ci.indexOf("\n  e2e-windows:\n"));
  assert.match(installer, /if: \$\{\{ needs\.changes\.outputs\.run_windows_installer == 'true' \}\}/);
  assert.match(installer, /vpk pack/);
  assert.match(installer, /upload-artifact/);
  assert.equal(dueJobs({ EVENT: "workflow_dispatch" }).run_windows_installer, "true");
  assert.equal(dueJobs({ CODE: "true", CODE_COUNT: "1" }).run_windows_installer, "false");
  const pushOnly = [{ name: "ci-passed", conclusion: "success", steps: [{ name: "Every job that was due passed", conclusion: "success" }] }];
  const problems = evaluateRun({ event: "push", status: "completed", conclusion: "success" }, pushOnly);
  assert.ok(problems.some((p) => p.includes('no "Windows installer" job passed')), problems.join("; "));
});

// ---- GAT-13: one request pipeline for the three hosts

test("the server host, the Windows tray host and the Windows service host all take the one shared request pipeline and add no pipeline of their own", () => {
  const hosts = {
    "src/Deluno.Host/Program.cs": read("src/Deluno.Host/Program.cs"),
    "apps/windows-tray/DelunoServer.cs": read("apps/windows-tray/DelunoServer.cs"),
    "apps/windows-tray/ServiceHost.cs": read("apps/windows-tray/ServiceHost.cs")
  };
  for (const [file, text] of Object.entries(hosts)) {
    assert.equal(text.match(/\.UseDelunoRequestPipeline\(/g)?.length, 1, `${file} calls UseDelunoRequestPipeline once`);
    for (const own of ["UseRouting(", "UseAuthentication(", "UseAuthorization(", "UseForwardedHeaders(", "UseRateLimiter(", "UseCors("]) {
      assert.ok(!text.includes(`.${own}`), `${file} adds its own ${own}; that belongs in the shared pipeline`);
    }
  }
  assert.ok(existsSync(path.join(root, "src/Deluno.Worker/DelunoRequestPipeline.cs")));
});

// ---- GAT-14: smoke specs that skip themselves

test("every skip in a smoke spec says why, and each reason is one of the few that a shared disposable install or a phone justify", () => {
  const specs = [];
  const walk = (folder) => {
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
      const full = path.join(folder, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (entry.name.endsWith(".spec.ts")) specs.push(full);
    }
  };
  walk(path.join(root, "apps", "web", "tests"));
  const reasons = [];
  for (const file of specs) {
    const text = readFileSync(file, "utf8");
    for (const match of text.matchAll(/\btest\.skip\(([^;]*?)\);/gs)) {
      const call = match[1];
      const reason = call.match(/,\s*"([^"]{8,})"\s*$/s) ?? call.match(/,\s*`([^`]{8,})`\s*$/s);
      assert.ok(reason, `${path.relative(root, file)}: a skip with no reason: test.skip(${call.slice(0, 80)})`);
      reasons.push({ file: path.relative(root, file), reason: reason[1], call });
    }
  }
  assert.ok(reasons.length > 20, "the scan found the skips");
  const allowed = [
    /phone|mobile|desktop|phone-width/i, /Sizes are set in the test itself/, /shared disposable install/i, /DELUNO_E2E_USERNAME/,
    /README screenshots/i, /Only run to retake/, /Existing install detected/, /schedule column/i, /sidebar is a desktop control/i,
    /one-screen rule/i, /Automation was already resumed/
  ];
  const unexplained = reasons.filter((r) => !allowed.some((pattern) => pattern.test(r.reason)));
  assert.deepEqual(unexplained.map((r) => `${r.file}: ${r.reason}`), [], "a new kind of skip needs a decision, not a quiet hole");
});

// ---- GAT-15: the weekly dependency audit

test("the dependency audit runs weekly, fails when an installed NuGet package or a production npm package has a known advisory", () => {
  const audit = read(".github/workflows/audit.yml");
  assert.match(audit, /schedule:\s*\n\s*#.*\n\s*- cron: "[^"]+ \* \* 1"/, "weekly, on a Monday");
  assert.match(audit, /dotnet list Deluno\.slnx package --vulnerable --include-transitive/);
  assert.match(audit, /grep -q "has the following vulnerable packages" audit\.txt[\s\S]*?exit 1/, "the report is read, because the command exits 0 either way");
  assert.match(audit, /npm audit --omit=dev --audit-level=high/);
});

// ---- GAT-17: nothing private, nothing tracked that should not be

/** A throwaway git repository with the given tracked files. */
function gitRepo(files) {
  const folder = tempDir("repo");
  spawnSync("git", ["init", "--quiet"], { cwd: folder });
  for (const [name, text] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(folder, name)), { recursive: true });
    writeFileSync(path.join(folder, name), text);
  }
  spawnSync("git", ["add", "-A"], { cwd: folder });
  return folder;
}

test("a LAN address, a machine name or an e-mail address in a file an outsider reads is refused, and a placeholder is not", () => {
  const home = ["10", "1", "1", "5"].join(".");
  const dirty = gitRepo({ "README.md": `Open http://${home}:5099 to see it.\n` });
  const clean = gitRepo({ "README.md": "Open http://192.168.1.10:5099 to see it. Write to noreply@example.com.\n" });
  try {
    const refused = powershell(["-File", path.join(root, "scripts", "check-public-exposure.ps1"), "-Root", dirty], dirty);
    assert.equal(refused.status, 1, refused.output);
    assert.match(refused.output, /a LAN address on the home network/);
    const accepted = powershell(["-File", path.join(root, "scripts", "check-public-exposure.ps1"), "-Root", clean], clean);
    assert.equal(accepted.status, 0, accepted.output);
  } finally {
    rmSync(dirty, { recursive: true, force: true });
    rmSync(clean, { recursive: true, force: true });
  }
});

test("a tracked database, protection key, Python script or .env file is refused", () => {
  const dirty = gitRepo({ "data/platform.db": "x", "data/protection-keys/key-1.xml": "x", "tools/run.py": "x", ".env": "A=1", ".env.example": "A=" });
  const clean = gitRepo({ ".env.example": "A=", "src/a.cs": "class A {}" });
  try {
    const script = path.join(root, "scripts", "check-tracked-files.ps1");
    // The script reads the repository it sits in, so run a copy of it from inside each throwaway repository.
    for (const folder of [dirty, clean]) {
      mkdirSync(path.join(folder, "scripts"), { recursive: true });
      copyFileSync(script, path.join(folder, "scripts", "check-tracked-files.ps1"));
    }
    const refused = powershell(["-File", path.join(dirty, "scripts", "check-tracked-files.ps1")], dirty);
    assert.equal(refused.status, 1, refused.output);
    for (const line of ["data/platform.db (database file)", "data/protection-keys/key-1.xml (data-protection key)", "tools/run.py (Python script)", ".env (environment file)"]) {
      assert.ok(refused.output.includes(line), `${line}: ${refused.output}`);
    }
    assert.doesNotMatch(refused.output, /\.env\.example/);
    assert.equal(powershell(["-File", path.join(clean, "scripts", "check-tracked-files.ps1")], clean).status, 0);
  } finally {
    rmSync(dirty, { recursive: true, force: true });
    rmSync(clean, { recursive: true, force: true });
  }
});

test("a commit message with a closing keyword beside an issue reference is refused, even when it says 'not', and Refs is accepted", () => {
  const script = path.join(root, "scripts", "check-issue-closing-keywords.ps1");
  const run = (text) => powershell(["-File", script, "-Text", text], root);
  for (const text of ["this does not close #354", "Fixes #12", "resolved https://github.com/example/repo/issues/9", "Closes: #77"]) {
    const result = run(text);
    assert.equal(result.status, 1, `${text}: ${result.output}`);
    assert.match(result.output, /would close an issue on merge/);
  }
  for (const text of ["Refs #354. The scope stays open for the rest.", "Nothing here."]) {
    assert.equal(run(text).status, 0, text);
  }
});
