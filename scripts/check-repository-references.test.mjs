import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { DECLARED, OLD_OWNER, findViolations } from "./check-repository-references.mjs";

function tree(files) {
  const root = mkdtempSync(path.join(os.tmpdir(), "repo-refs-"));
  for (const [name, text] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, name)), { recursive: true });
    writeFileSync(path.join(root, name), text);
  }
  return root;
}

const written = (casing = (s) => s) => `see https://github.com/${casing(OLD_OWNER)}/Deluno`;

test("the old owner in a source, script, workflow, installer, Dockerfile, props or root json file is reported with its line", () => {
  const root = tree({
    "src/A/Thing.cs": `// one\n// ${written()}\n`,
    "apps/web/src/x.ts": written(),
    "installer/c.json": written(),
    "scripts/s.ps1": written(),
    ".github/workflows/w.yml": written(),
    "Dockerfile.api": written(),
    "docker-compose.dev.yml": written(),
    "Directory.Build.props": written(),
    "package.json": written(),
  });
  try {
    const found = findViolations(root);
    assert.deepEqual(found.map((v) => v.file).sort(), [
      ".github/workflows/w.yml", "Directory.Build.props", "Dockerfile.api", "apps/web/src/x.ts", "docker-compose.dev.yml",
      "installer/c.json", "package.json", "scripts/s.ps1", "src/A/Thing.cs",
    ]);
    assert.equal(found.find((v) => v.file === "src/A/Thing.cs").line, 2);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("the match ignores case", () => {
  const root = tree({ "src/a.cs": written((s) => s.toUpperCase()) });
  try { assert.equal(findViolations(root).length, 1); } finally { rmSync(root, { recursive: true, force: true }); }
});

test("the declared single-value files and the pending-flip files may name it", () => {
  const files = Object.fromEntries(DECLARED.map((f) => [f, written()]));
  files[".github/ISSUE_TEMPLATE/config.yml"] = written();
  const root = tree(files);
  try { assert.deepEqual(findViolations(root), []); } finally { rmSync(root, { recursive: true, force: true }); }
});

test("documents, tests, build output and installed packages are out of scope", () => {
  const root = tree({
    "docs/a.md": written(), "README.md": written(), "src/notes.md": written(), "tests/t.cs": written(),
    "src/A/bin/x.json": written(), "apps/web/node_modules/p/i.js": written(), "other.yml": written(),
  });
  try { assert.deepEqual(findViolations(root), []); } finally { rmSync(root, { recursive: true, force: true }); }
});

test("the real repository has no stray reference", () => {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  assert.deepEqual(findViolations(root), []);
});
