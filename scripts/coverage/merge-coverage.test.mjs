import { test } from "node:test";
import assert from "node:assert/strict";
import {
  parseCobertura, parseLcov, mergeFiles, buildReport, normalizePath, findExclusion, areaOf, renderTable
} from "./merge-coverage.mjs";

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
