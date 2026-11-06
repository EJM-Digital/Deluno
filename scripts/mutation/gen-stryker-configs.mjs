#!/usr/bin/env node
// Writes (or, with --check, verifies) one stryker-config.json per .NET production project, next to its csproj.
// Each config points at the test projects that cover the project, taken from the csproj reference graph.
//
//   node scripts/mutation/gen-stryker-configs.mjs           write the configs
//   node scripts/mutation/gen-stryker-configs.mjs --check   fail when a config is missing or out of date
//
// Run Stryker from the repository root:  dotnet tool run dotnet-stryker -f src/<Project>/stryker-config.json
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { coverage } from "./dotnet-projects.mjs";
import { filterFor, testClasses, typeNames } from "./test-filter.mjs";

// Tray.Tests only holds tests on Windows (the tray is a Windows assembly), so it covers nothing on Linux CI.
export function configFor(prod, tests, projects, filter = null) {
  const testProjects = tests.filter((t) => prod === "Deluno.Tray" || t !== "Deluno.Tray.Tests");
  return {
    "stryker-config": {
      project: `${prod}.csproj`,
      "test-projects": testProjects.map((t) => projects[t]),
      ...(filter ? { "test-case-filter": filter } : {}),
      reporters: ["json", "progress"],
      mutate: ["!**/Migrations/**"],
      "mutation-level": "Standard",
      thresholds: { high: 100, low: 90, break: 0 },
      "ignore-mutations": [],
      "disable-bail": false
    }
  };
}

export function expected(root = ".") {
  const { projects, coverage: cov } = coverage(root);
  const out = {};
  const classes = testClasses(root);
  for (const [prod, tests] of Object.entries(cov)) {
    const dir = projects[prod].replace(/[^/]+$/, "");
    out[`${dir}stryker-config.json`] = configFor(prod, tests, projects, filterFor(typeNames(root, dir), classes));
  }
  return out;
}

// `risky` is the parsed mutation-risky-areas.json, or null for every project.
export function matrixEntries(configFiles, risky) {
  const entries = [];
  for (const file of configFiles) {
    const dir = file.split("/").slice(-2)[0];
    const project = dir === "windows-tray" ? "Deluno.Tray" : dir;
    const entry = { project, config: file, os: project === "Deluno.Tray" ? "windows-latest" : "ubuntu-latest" };
    if (risky && !Object.hasOwn(risky.projects ?? {}, project)) {
      const folders = (risky.files ?? []).filter((f) => f.project === project).map((f) => f.path);
      if (folders.length === 0) continue;
      // Stryker globs are relative to the project; "!**/Migrations/**" is the config's own exclusion, which a command-line list replaces.
      entry.mutate = [...folders.map((p) => (p.endsWith("/") ? `**/${p}**/*.cs` : `**/${p}`)), "!**/Migrations/**"].join(",");
    }
    entries.push(entry);
  }
  return entries;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const check = process.argv.includes("--check");
  if (process.argv.includes("--matrix")) {
    // The mutation workflow's job matrix: one entry per production project. The tray is Windows-only.
    // `--matrix risky` keeps only the projects that hold a risky area (docs/testing/mutation-risky-areas.json); a project
    // that is risky only in some folders gets a `mutate` list (comma-separated Stryker globs) so the job mutates just those.
    const scope = process.argv[process.argv.indexOf("--matrix") + 1] === "risky" ? "risky" : "all";
    const risky = scope === "risky" ? JSON.parse(readFileSync("docs/testing/mutation-risky-areas.json", "utf8")) : null;
    console.log(JSON.stringify({ include: matrixEntries(Object.keys(expected()), risky) }));
    process.exit(0);
  }
  const problems = [];
  for (const [file, config] of Object.entries(expected())) {
    const text = JSON.stringify(config, null, 2) + "\n";
    if (check) {
      if (!existsSync(file)) problems.push(`${file} is missing; run scripts/mutation/gen-stryker-configs.mjs.`);
      else if (readFileSync(file, "utf8").replace(/\r\n/g, "\n") !== text) problems.push(`${file} is out of date; run scripts/mutation/gen-stryker-configs.mjs.`);
    } else {
      writeFileSync(file, text);
    }
  }
  if (problems.length) {
    for (const p of problems) console.error(`FAIL: ${p}`);
    process.exit(1);
  }
  console.log(check ? "Every production project has an up-to-date Stryker config." : "Stryker configs written.");
}

