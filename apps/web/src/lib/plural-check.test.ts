import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createRequire } from "node:module";
import { afterAll, describe, expect, it } from "vitest";

const require = createRequire(import.meta.url);
const tsNode = require.resolve("ts-node/dist/bin.js");
const script = join(process.cwd(), "scripts", "validate-plurals.ts");
const folders: string[] = [];

/** Runs the build's plural check on one sample file; returns its exit code and what it printed. */
function check(source: string): { code: number; output: string } {
  const folder = mkdtempSync(join(tmpdir(), "deluno-plural-check-"));
  folders.push(folder);
  writeFileSync(join(folder, "sample.tsx"), source);
  try {
    const output = execFileSync(process.execPath, [tsNode, script, folder], { encoding: "utf8", stdio: "pipe" });
    return { code: 0, output };
  } catch (error) {
    const failed = error as { status: number; stderr: string; stdout: string };
    return { code: failed.status, output: `${failed.stdout}${failed.stderr}` };
  }
}

afterAll(() => { for (const folder of folders) rmSync(folder, { recursive: true, force: true }); });

describe("the build's check for a hand-built plural (#1423)", () => {
  it.each([
    ["an s added when the count is not 1", "const a = `${n} title${n === 1 ? \"\" : \"s\"}`;"],
    ["an s added with the test the other way round", "const a = `${n} title${n !== 1 ? \"s\" : \"\"}`;"],
    ["an s added when the count is more than 1", "const a = `${n} title${n > 1 ? \"s\" : \"\"}`;"],
    ["an es added", "const a = `${n} match${n === 1 ? \"\" : \"es\"}`;"],
    ["y or ies chosen", "const a = `${n} entr${n === 1 ? \"y\" : \"ies\"}`;"],
    ["a word and its plural written out", "const a = `${n} ${n === 1 ? \"library\" : \"libraries\"}`;"],
    ["a word and the same word with an s", "const a = `${n} ${n === 1 ? \"title\" : \"titles\"}`;"],
    ["an s added with &&", "const a = `${n} title${n !== 1 && \"s\"}`;"],
    ["a hand-built plural in JSX", "const a = <p>{n} title{n === 1 ? \"\" : \"s\"}</p>;"]
  ])("fails on %s", (_name, source) => {
    const result = check(source);
    expect(result.code).toBe(1);
    expect(result.output).toContain("sample.tsx:1");
  });

  it("passes the shared helper, a comment that shows the pattern, and a line that says why it keeps its own wording", () => {
    const result = check([
      "const a = plural(n, \"movie library\");",
      "// a count was written n === 1 ? \"\" : \"s\" here once",
      "/* n === 1 ? \"\" : \"s\" */",
      "// plural-ok: the sentence changes its verb, not only its noun",
      "const b = n === 1 ? \"a\" : \"b\" + (n === 1 ? \"\" : \"s\");"
    ].join("\n"));
    expect(result.code).toBe(0);
    expect(result.output).toContain("Every counted phrase goes through plural()");
  });
});
