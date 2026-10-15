// Which test projects cover which production project, worked out from the csproj ProjectReference graph
// (a test covers a production project when it references it directly or through another reference).
// Used by gen-stryker-configs.mjs and check-mutation-config.mjs (issue 1269, docs/testing/STRATEGY.md measure 3).
import { readFileSync, readdirSync, existsSync } from "node:fs";
import path from "node:path";

export function findProjects(root = ".") {
  const projects = {};
  for (const dir of ["src", "tests"]) {
    if (!existsSync(path.join(root, dir))) continue;
    for (const name of readdirSync(path.join(root, dir))) {
      const file = path.join(dir, name, `${name}.csproj`).replace(/\\/g, "/");
      if (existsSync(path.join(root, file))) projects[name] = file;
    }
  }
  const tray = "apps/windows-tray/Deluno.Tray.csproj";
  if (existsSync(path.join(root, tray))) projects["Deluno.Tray"] = tray;
  return projects;
}

export function referencesOf(root, projects, name) {
  const text = readFileSync(path.join(root, projects[name]), "utf8");
  return [...text.matchAll(/ProjectReference Include="([^"]+)"/g)]
    .map((m) => path.basename(m[1].replace(/\\/g, "/"), ".csproj"))
    .filter((x) => projects[x]);
}

export function closure(root, projects, name, seen = new Set()) {
  for (const ref of referencesOf(root, projects, name)) {
    if (!seen.has(ref)) {
      seen.add(ref);
      closure(root, projects, ref, seen);
    }
  }
  return seen;
}

// { "Deluno.Contracts": ["Deluno.Movies.Tests", ...] } for every production project under src/ and the tray.
export function coverage(root = ".") {
  const projects = findProjects(root);
  const tests = Object.keys(projects).filter((n) => n.endsWith(".Tests"));
  const result = {};
  for (const prod of Object.keys(projects).filter((n) => !n.endsWith(".Tests"))) {
    result[prod] = tests.filter((t) => closure(root, projects, t).has(prod));
  }
  return { projects, coverage: result };
}
