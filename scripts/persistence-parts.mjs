#!/usr/bin/env node
// How Deluno.Persistence.Tests is split into parallel parts (issue 1346). The suite outgrew one CI job: with
// coverage it ran more than 54 minutes. One rule, used by CI (.github/workflows/ci.yml, job backend) and by the
// local gate (scripts/gate.ps1), so both run the same parts:
//
//   A test belongs to the part that lists the first segment of its namespace under Deluno.Persistence.Tests,
//   e.g. Deluno.Persistence.Tests.Api.* is part 1. The last part lists none: it takes every namespace the other
//   parts do not name (and the root namespace), so a new folder of tests lands in a part without anyone editing
//   this file. Every test therefore runs in exactly one part, by construction; check-persistence-parts.mjs
//   proves it against `dotnet test --list-tests`.
//
// Usage:
//   node scripts/persistence-parts.mjs count             -> the number of parts
//   node scripts/persistence-parts.mjs filter <1..N>     -> the dotnet test --filter expression for that part
//
// The filter is only the namespace rule; callers add their own such as Category!=Timing.
import { pathToFileURL } from "node:url";
import { resolve } from "node:path";

const ROOT = "Deluno.Persistence.Tests";

// Weights are test counts on 17 Oct 2026 (Api about 1,400 tests, the others about 1,700 and 1,300).
export const PARTS = [
  { name: "Api", namespaces: ["Api"] },
  { name: "Integrations, Platform, Quality, Jobs, Security", namespaces: ["Integrations", "Platform", "Quality", "Jobs", "Security"] },
  { name: "Everything else", namespaces: null },
];

const term = (namespace) => `FullyQualifiedName~${ROOT}.${namespace}.`;

export function filterFor(part) {
  if (!Number.isInteger(part) || part < 1 || part > PARTS.length) throw new Error(`part must be 1 to ${PARTS.length}`);
  const own = PARTS[part - 1].namespaces;
  if (own) return own.map(term).join("|");
  const named = PARTS.flatMap((p) => p.namespaces ?? []);
  return named.map((n) => `FullyQualifiedName!~${ROOT}.${n}.`).join("&");
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const [command, arg] = process.argv.slice(2);
  if (command === "count") console.log(PARTS.length);
  else if (command === "filter") console.log(filterFor(Number(arg)));
  else { console.error("usage: persistence-parts.mjs count | filter <part>"); process.exit(2); }
}
