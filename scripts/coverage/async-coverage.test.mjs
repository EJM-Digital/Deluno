// Proves tests/coverage.runsettings measures the body of async methods (issue 1450). The C# compiler moves an async
// method's body into a hidden [CompilerGenerated] state machine; excluding that attribute dropped most of the backend
// from the report. This builds a tiny library and test project in a temp folder, runs the real `dotnet test` with the
// real runsettings and reads the opencover report: a fully covered async method must show hit lines, and an awaited
// branch no test reaches must show as a line with zero hits (before the fix it was missing, so it counted as 100%).
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, readdirSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const SETTINGS = fileURLToPath(new URL("../../tests/coverage.runsettings", import.meta.url));

const LIB_CSPROJ = `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>Deluno.CoverageProbe</AssemblyName></PropertyGroup></Project>`;
// Line numbers matter: CoveredLine and UncoveredLine are asserted below.
const PROBE_CS = [
  "namespace Deluno.CoverageProbe;",
  "public static class Probe",
  "{",
  "    public static async Task<int> Covered(int x)",
  "    {",
  "        await Task.Yield();",
  "        return x + 1;",
  "    }",
  "    public static async Task<int> Partial(bool take)",
  "    {",
  "        if (take)",
  "        {",
  "            await Task.Yield();",
  "            return 1;",
  "        }",
  "        return 2;",
  "    }",
  "}",
  ""
].join("\n");
const COVERED_LINE = 7;
const UNCOVERED_LINE = 14;

const TEST_CSPROJ = `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable></PropertyGroup>
<ItemGroup><PackageReference Include="coverlet.collector" Version="6.0.4" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="xunit" Version="2.9.3" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" /></ItemGroup>
<ItemGroup><ProjectReference Include="../lib/lib.csproj" /></ItemGroup></Project>`;
const TESTS_CS = `using Deluno.CoverageProbe;
using Xunit;
public class ProbeTests
{
    [Fact] public async Task Covered_is_called() => Assert.Equal(2, await Probe.Covered(1));
    [Fact] public async Task Partial_skips_the_await() => Assert.Equal(2, await Probe.Partial(false));
}
`;

function walk(dir, name, found = []) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) walk(p, name, found);
    else if (e.name === name) found.push(p);
  }
  return found;
}

test("async method bodies are measured and attributed to their source lines", { timeout: 240000 }, () => {
  const root = mkdtempSync(join(tmpdir(), "deluno-coverage-probe-"));
  try {
    mkdirSync(join(root, "lib"));
    mkdirSync(join(root, "tests"));
    writeFileSync(join(root, "lib", "lib.csproj"), LIB_CSPROJ);
    writeFileSync(join(root, "lib", "Probe.cs"), PROBE_CS);
    writeFileSync(join(root, "tests", "tests.csproj"), TEST_CSPROJ);
    writeFileSync(join(root, "tests", "ProbeTests.cs"), TESTS_CS);
    const run = spawnSync("dotnet", ["test", join(root, "tests", "tests.csproj"), "--collect:XPlat Code Coverage", "--settings", SETTINGS, "--nologo", "-v", "q"], {
      encoding: "utf8", cwd: root, env: { ...process.env, MSBUILDDISABLENODEREUSE: "1", DOTNET_NOLOGO: "1" }
    });
    assert.equal(run.status, 0, `dotnet test failed:\n${run.stdout}\n${run.stderr}`);
    const reports = walk(join(root, "tests"), "coverage.opencover.xml");
    assert.equal(reports.length, 1, "one opencover report");
    const xml = readFileSync(reports[0], "utf8");
    assert.ok(/<Method[^>]*>\s*<Name>[^<]*MoveNext/.test(xml) || xml.includes("MoveNext"), "the async state machine's MoveNext is in the report");
    const hits = new Map();
    for (const m of xml.matchAll(/<SequencePoint [^>]*?vc="(\d+)"[^>]*?sl="(\d+)"/g)) {
      const line = Number(m[2]);
      hits.set(line, Math.max(hits.get(line) ?? 0, Number(m[1])));
    }
    if (hits.size === 0) {
      for (const m of xml.matchAll(/<SequencePoint [^>]*?sl="(\d+)"[^>]*?vc="(\d+)"/g)) {
        const line = Number(m[1]);
        hits.set(line, Math.max(hits.get(line) ?? 0, Number(m[2])));
      }
    }
    assert.ok(hits.get(COVERED_LINE) > 0, "the line after the await in a fully covered async method is hit");
    assert.equal(hits.get(UNCOVERED_LINE), 0, "the awaited branch no test reaches is measured, with no hits");
    assert.ok(existsSync(SETTINGS));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the runsettings keep generated code out but not the compiler's async state machines", () => {
  const xml = readFileSync(SETTINGS, "utf8").replace(/<!--[\s\S]*?-->/g, "");
  const attrs = /<ExcludeByAttribute>([^<]*)<\/ExcludeByAttribute>/.exec(xml)[1];
  assert.match(attrs, /GeneratedCodeAttribute/);
  assert.doesNotMatch(attrs, /CompilerGeneratedAttribute/);
  assert.match(xml, /<ExcludeByFile>\*\*\/obj\/\*\*\/\*<\/ExcludeByFile>/);
});
