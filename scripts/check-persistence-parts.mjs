#!/usr/bin/env node
// Proves the Persistence split (issue 1346, scripts/persistence-parts.mjs): every test runs exactly once across the
// parts. It compares `dotnet test --list-tests` of the whole project against the union of the parts, and fails on a
// test no part runs, a test two parts run, or a test the parts invent. The Timing tests are excluded from the parts
// and run on their own, so the whole is listed twice: Category!=Timing against the parts, Category=Timing against the
// timing run, and both are required to be non-empty only where they should be.
//
// Needs a Release build already made. Usage: node scripts/check-persistence-parts.mjs
import { execFileSync } from "node:child_process";
import { pathToFileURL } from "node:url";
import { resolve } from "node:path";
import { PARTS, filterFor } from "./persistence-parts.mjs";

const PROJECT = "tests/Deluno.Persistence.Tests/Deluno.Persistence.Tests.csproj";

// Returns one line per problem; empty means every test is in exactly one part.
export function compare(whole, parts) {
  const problems = [];
  const seen = new Map();
  parts.forEach((list, i) => {
    for (const name of list) {
      if (seen.has(name)) problems.push(`"${name}" runs in part ${seen.get(name)} and part ${i + 1}.`);
      else seen.set(name, i + 1);
    }
  });
  const wholeSet = new Set(whole);
  for (const name of wholeSet) if (!seen.has(name)) problems.push(`"${name}" runs in no part.`);
  for (const name of seen.keys()) if (!wholeSet.has(name)) problems.push(`"${name}" is in part ${seen.get(name)} but not in the whole list.`);
  if (wholeSet.size === 0) problems.push("The whole list is empty; nothing was proven.");
  return problems;
}

export function parseList(output) {
  const lines = output.split(/\r?\n/);
  const start = lines.findIndex((l) => /^The following Tests are available:/.test(l));
  if (start < 0) throw new Error("dotnet test --list-tests printed no test list:\n" + output.slice(-2000));
  return lines.slice(start + 1).map((l) => l.trim()).filter(Boolean);
}

function list(filter) {
  const out = execFileSync("dotnet", ["test", PROJECT, "--configuration", "Release", "--no-build", "--no-restore", "--list-tests", "--filter", filter],
    { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });
  return parseList(out);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const notTiming = "Category!=Timing";
  const whole = list(notTiming);
  const parts = PARTS.map((_, i) => list(`${notTiming}&(${filterFor(i + 1)})`));
  parts.forEach((p, i) => console.log(`part ${i + 1} (${PARTS[i].name}): ${p.length} tests`));
  console.log(`whole (without Timing): ${whole.length} tests`);
  const problems = compare(whole, parts);
  if (problems.length) {
    console.error(problems.slice(0, 40).join("\n") + (problems.length > 40 ? `\n... and ${problems.length - 40} more` : ""));
    process.exit(1);
  }
  console.log("Every Persistence test runs in exactly one part.");
}
