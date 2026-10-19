// GAT-07 (#1361): every Node test file in the repository is named in a job of .github/workflows/ci.yml, so none is run by
// nobody. A file named only through a glob does not count: the job must be able to fail for that file alone.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export function testFilesNotInWorkflow(files, workflowText) {
  return files.filter((file) => !workflowText.includes(file));
}

export function listTestFiles(root) {
  const out = execFileSync("git", ["ls-files", "*.test.mjs"], { cwd: root, encoding: "utf8" });
  return out.split(/\r?\n/).filter(Boolean).filter((f) => !f.startsWith("node_modules/") && !f.includes("/node_modules/"));
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const root = join(dirname(fileURLToPath(import.meta.url)), "..");
  const workflow = readFileSync(join(root, ".github/workflows/ci.yml"), "utf8");
  const missing = testFilesNotInWorkflow(listTestFiles(root), workflow);
  if (missing.length) {
    console.error("These Node test files are run by no job in ci.yml:\n  " + missing.join("\n  "));
    process.exit(1);
  }
  console.log("Every Node test file is run by a CI job.");
}
