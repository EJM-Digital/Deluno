import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  parseCobertura, parseLcov, mergeFiles, buildReport, normalizePath, findExclusion, areaOf, renderTable, pct
} from "./merge-coverage.mjs";

const SCRIPT = fileURLToPath(new URL("./merge-coverage.mjs", import.meta.url));

const COBERTURA = `<?xml version="1.0"?><coverage><sources><source>/work/Deluno</source></sources><packages><package name="Deluno.Movies"><classes>
<class name="Deluno.Movies.A" filename="/work/Deluno/src/Deluno.Movies/A.cs"><methods/><lines>
<line number="1" hits="3" branch="False"/>
<line number="2" hits="0" branch="True" condition-coverage="50% (1/2)"/>
<line number="3" hits="0" branch="False"/>
</lines></class>
<class name="Deluno.Movies.A/&lt;&gt;c" filename="/work/Deluno/src/Deluno.Movies/A.cs"><lines>
<line number="2" hits="2" branch="True" condition-coverage="100% (2/2)"/>
<line number="3" hits="1" branch="False"/>
</lines></class>
<class name="Deluno.Movies.Program" filename="C:\\work\\Deluno\\src\\Deluno.Host\\Program.cs"><lines>
<line number="1" hits="0" branch="False"/>
</lines></class>
</classes></package></packages></coverage>`;

const LCOV = `TN:
SF:src/routes/home.tsx
DA:1,1
DA:2,0
BRDA:2,0,0,1
BRDA:2,0,1,-
BRDA:2,1,0,0
end_of_record
SF:/work/Deluno/scripts/x.mjs
DA:5,2
end_of_record
`;

test("cobertura: lines and branches are unioned across the classes of one file", () => {
  const files = parseCobertura(COBERTURA, { repoRoot: "/work/Deluno" });
  const a = files.get("src/Deluno.Movies/A.cs");
  assert.deepEqual([...a.lines], [[1, 3], [2, 2], [3, 1]]);
  assert.deepEqual(a.branches.get(2), { covered: 2, total: 2 });
  assert.ok(files.has("src/Deluno.Host/Program.cs"), "a Windows path is made repository-relative");
});

test("lcov: relative paths get the prefix, absolute ones lose the repository root, branches count per line", () => {
  const files = parseLcov(LCOV, { repoRoot: "/work/Deluno", prefix: "apps/web" });
  assert.deepEqual([...files.keys()], ["apps/web/src/routes/home.tsx", "scripts/x.mjs"]);
  assert.deepEqual(files.get("apps/web/src/routes/home.tsx").branches.get(2), { covered: 1, total: 3 });
});

test("normalizePath handles a foreign absolute root and a plain relative path", () => {
  assert.equal(normalizePath("/home/runner/work/Deluno/Deluno/src/Deluno.Api/X.cs", "/elsewhere"), "src/Deluno.Api/X.cs");
  assert.equal(normalizePath("./src/a.ts", "", "apps/web"), "apps/web/src/a.ts");
  assert.equal(normalizePath(String.raw`D:\w\Deluno\apps\web\src\a.ts`, "D:/w/Deluno"), "apps/web/src/a.ts");
});

test("merging two reports of one file keeps the best hit and the best branch coverage", () => {
  const one = parseLcov("SF:a.ts\nDA:1,0\nDA:2,1\nBRDA:2,0,0,0\nBRDA:2,0,1,0\nend_of_record\n");
  const two = parseLcov("SF:a.ts\nDA:1,4\nBRDA:2,0,0,1\nBRDA:2,0,1,0\nend_of_record\n");
  const f = mergeFiles(one, two).get("a.ts");
  assert.equal(f.lines.get(1), 4);
  assert.deepEqual(f.branches.get(2), { covered: 1, total: 2 });
});

test("the report is per file with totals and areas; an excluded file is listed with its reason, not dropped", () => {
  const files = mergeFiles(parseCobertura(COBERTURA, { repoRoot: "/work/Deluno" }));
  const report = buildReport(files, [{ path: "src/Deluno.Host/Program.cs", reason: "Process entry point glue." }]);
  assert.deepEqual(Object.keys(report.files), ["src/Deluno.Movies/A.cs"]);
  assert.deepEqual(report.files["src/Deluno.Movies/A.cs"].lines, { covered: 3, total: 3 });
  assert.deepEqual(report.excluded, [{ file: "src/Deluno.Host/Program.cs", reason: "Process entry point glue." }]);
  assert.deepEqual(Object.keys(report.areas), ["src/Deluno.Movies"]);
  assert.deepEqual(report.totals.branches, { covered: 2, total: 2 });
  assert.match(renderTable(report), /src\/Deluno.Movies\/A.cs \| 100.0% \| 100.0% \| 3\/3 \| 2\/2/);
  assert.match(renderTable(report), /Program.cs: Process entry point glue/);
});

test("exclusion globs: * stays in a folder, ** crosses folders", () => {
  const ex = [{ path: "src/*/Program.cs", reason: "r" }, { path: "apps/web/src/gen/**", reason: "r" }];
  assert.ok(findExclusion(ex, "src/Deluno.Host/Program.cs"));
  assert.ok(!findExclusion(ex, "src/Deluno.Host/sub/Program.cs"));
  assert.ok(findExclusion(ex, "apps/web/src/gen/a/b.ts"));
  assert.ok(!findExclusion(ex, "apps/web/src/other.ts"));
});

test("areas group by project folder", () => {
  assert.equal(areaOf("src/Deluno.Api/X.cs"), "src/Deluno.Api");
  assert.equal(areaOf("apps/web/src/a.ts"), "apps/web");
  assert.equal(areaOf("scripts/a.mjs"), "scripts");
});

test("cobertura: a relative filename is resolved against the first <source>; classes without a filename or bad line numbers are skipped", () => {
  const xml = `<coverage><sources><source>C:/w/Deluno/</source></sources><packages><package><classes>
<class name="X" filename="src/Deluno.Api/X.cs"><lines>
<line number="1" hits="2" branch="True" condition-coverage="50% (1/2)"/>
<line number="oops" hits="9"/>
<line number="2" hits="x" branch="false" condition-coverage="0% (0/2)"/>
</lines></class>
<class name="NoFile"><lines><line number="1" hits="1"/></lines></class>
</classes></package></packages></coverage>`;
  const files = parseCobertura(xml, { repoRoot: "C:/w/Deluno" });
  assert.deepEqual([...files.keys()], ["src/Deluno.Api/X.cs"]);
  const f = files.get("src/Deluno.Api/X.cs");
  assert.deepEqual([...f.lines], [[1, 2], [2, 0]]);
  assert.deepEqual([...f.branches], [[1, { covered: 1, total: 2 }]], "branch only counted when branch=true");
});


test("merging keeps the wider branch set and the higher covered count at equal width", () => {
  const narrow = parseLcov("SF:a.ts\nBRDA:1,0,0,1\nend_of_record\n");
  const wide = parseLcov("SF:a.ts\nBRDA:1,0,0,0\nBRDA:1,0,1,1\nend_of_record\n");
  assert.deepEqual(mergeFiles(narrow, wide).get("a.ts").branches.get(1), { covered: 1, total: 2 });
  const more = parseLcov("SF:a.ts\nBRDA:1,0,0,1\nBRDA:1,0,1,1\nend_of_record\n");
  assert.deepEqual(mergeFiles(wide, more).get("a.ts").branches.get(1), { covered: 2, total: 2 });
});

test("lcov ignores data outside a record and a record with no data adds no branches", () => {
  const files = parseLcov("DA:1,1\nBRDA:1,0,0,1\nSF:b.ts\nend_of_record\nDA:3,1\n");
  assert.equal(files.get("b.ts").lines.size, 0);
  assert.equal(files.get("b.ts").branches.size, 0);
});

test("pct is null for nothing to cover; tables show n/a, not a number", () => {
  assert.equal(pct({ covered: 0, total: 0 }), null);
  assert.equal(pct({ covered: 1, total: 4 }), 25);
  const report = buildReport(mergeFiles(parseLcov("SF:scripts/empty.mjs\nend_of_record\n")));
  assert.match(renderTable(report), /scripts\/empty.mjs \| n\/a \| n\/a \| 0\/0 \| 0\/0/);
  assert.match(renderTable(report), /Total: line n\/a/);
  assert.ok(!renderTable(report).includes("Excluded by"));
});

test("exclusion globs: ? matches one character, regex characters are literal, a missing reason is empty", () => {
  const ex = [{ path: "src/a?.c+s", reason: "" }, { path: 42 }];
  assert.ok(findExclusion(ex, "src/ab.c+s"));
  assert.ok(!findExclusion(ex, "src/abb.c+s"));
  assert.ok(!findExclusion(ex, "src/ab.ccs"));
  const files = parseLcov("SF:src/ab.c+s\nDA:1,1\nend_of_record\n");
  assert.deepEqual(buildReport(files, [{ path: "src/ab.c+s" }]).excluded, [{ file: "src/ab.c+s", reason: "" }]);
});

test("areaOf keeps one level for a file directly under a project root", () => {
  assert.equal(areaOf("src/x.cs"), "src");
  assert.equal(areaOf("services/Deluno.X/y.cs"), "services/Deluno.X");
});

test("normalizePath leaves an absolute path with no known top folder as it is, and applies a backslash prefix", () => {
  assert.equal(normalizePath("/opt/other/file.ts", "/work"), "/opt/other/file.ts");
  assert.equal(normalizePath("a.ts", "", "apps\\web\\"), "apps/web/a.ts");
});

function runCli(args, cwd) {
  return spawnSync(process.execPath, [SCRIPT, ...args], { cwd, encoding: "utf8" });
}

test("command line: merges a cobertura and lcov files with and without a prefix, applies exclusions, writes json and markdown", () => {
  const dir = mkdtempSync(join(tmpdir(), "merge-cov-"));
  try {
    writeFileSync(join(dir, "c.xml"), COBERTURA);
    writeFileSync(join(dir, "l.info"), LCOV);
    writeFileSync(join(dir, "ex.json"), JSON.stringify({ exclusions: [{ path: "src/Deluno.Host/Program.cs", reason: "glue" }] }));
    const r = runCli([
      "--cobertura", join(dir, "c.xml"), "--lcov", `${join(dir, "l.info")}=apps/web`, "--lcov", join(dir, "l.info"),
      "--exclusions", join(dir, "ex.json"), "--repo-root", "/work/Deluno",
      "--out-json", join(dir, "out", "r.json"), "--out-md", join(dir, "out", "r.md")
    ], dir);
    assert.equal(r.status, 0, r.stderr);
    assert.match(r.stdout, /Merged \d+ production files, 1 excluded\./);
    assert.match(r.stdout, /Line .*%, branch .*%\./);
    const json = JSON.parse(readFileSync(join(dir, "out", "r.json"), "utf8"));
    assert.ok(json.files["src/Deluno.Movies/A.cs"]);
    assert.ok(json.files["apps/web/src/routes/home.tsx"]);
    assert.equal(json.excluded[0].reason, "glue");
    assert.match(readFileSync(join(dir, "out", "r.md"), "utf8"), /Excluded by docs\/testing\/coverage-exclusions.json/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("command line: with no exclusions or outputs it still reports; no report or a bad switch fails with a plain message", () => {
  const dir = mkdtempSync(join(tmpdir(), "merge-cov-"));
  try {
    writeFileSync(join(dir, "l.info"), "SF:scripts/a.mjs\nDA:1,1\nend_of_record\n");
    const ok = runCli(["--lcov", join(dir, "l.info")], dir);
    assert.equal(ok.status, 0, ok.stderr);
    assert.match(ok.stdout, /Merged 1 production files, 0 excluded\./);
    assert.match(ok.stdout, /Line 100.0%, branch n\/a\./);
    const none = runCli([], dir);
    assert.equal(none.status, 1);
    assert.match(none.stderr, /Give at least one --cobertura or --lcov report\./);
    const bad = runCli(["--nope"], dir);
    assert.equal(bad.status, 1);
    assert.match(bad.stderr, /Unknown argument --nope/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
