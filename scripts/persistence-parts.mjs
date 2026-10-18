#!/usr/bin/env node
// How Deluno.Persistence.Tests is split into parallel parts (issue 1346). The suite outgrew one CI job: with
// coverage it ran more than 54 minutes. One rule, used by CI (.github/workflows/ci.yml, job backend) and by the
// local gate (scripts/gate.ps1), so both run the same parts:
//
//   A test belongs to the part that lists the first segment of its namespace under Deluno.Persistence.Tests,
//   e.g. Deluno.Persistence.Tests.Integrations.* is part 5. The Api namespace is the one big folder (about 1,800
//   tests, about 30 minutes in one job: CI run 39100172223 hit the 40 minute limit), so it is split by the FIRST
//   LETTER of the test class's name: parts 1 to 3 list their letters, part 4 takes every other Api class, so a new
//   class lands in a part without anyone editing this file. The last part lists no namespace: it takes every namespace
//   the other parts do not name (and the root namespace), so a new folder of tests lands in a part too. Every test
//   therefore runs in exactly one part, by construction; check-persistence-parts.mjs proves it against
//   `dotnet test --list-tests`.
//
// Usage:
//   node scripts/persistence-parts.mjs count             -> the number of parts
//   node scripts/persistence-parts.mjs filter <1..N>     -> the dotnet test --filter expression for that part
//
// The filter is only the namespace rule; callers add their own such as Category!=Timing.
import { pathToFileURL } from "node:url";
import { resolve } from "node:path";

const ROOT = "Deluno.Persistence.Tests";

// Weights are test counts on 18 Oct 2026 (Api 1,908 tests at about one second each, in four parts of 413 to 514, about
// 7 to 9 minutes; the others about 1,700 and 1,300 tests in 4 to 9 minutes).
export const PARTS = [
  { name: "Api A", namespaces: ["Api"], classLetters: ["A"] },
  { name: "Api B to F", namespaces: ["Api"], classLetters: ["B", "C", "D", "E", "F"] },
  { name: "Api G to Q", namespaces: ["Api"], classLetters: ["G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q"] },
  { name: "Api R to Z and the rest", namespaces: ["Api"], classLetters: null },
  { name: "Integrations, Platform, Quality, Jobs, Security", namespaces: ["Integrations", "Platform", "Quality", "Jobs", "Security"] },
  { name: "Everything else", namespaces: null },
];

const term = (namespace) => `FullyQualifiedName~${ROOT}.${namespace}.`;
const letterTerm = (letter) => `${ROOT}.Api.${letter}`;
const apiLettersNamed = () => PARTS.flatMap((p) => p.classLetters ?? []);

export function filterFor(part) {
  if (!Number.isInteger(part) || part < 1 || part > PARTS.length) throw new Error(`part must be 1 to ${PARTS.length}`);
  const { namespaces, classLetters } = PARTS[part - 1];
  if (classLetters) return classLetters.map((l) => `FullyQualifiedName~${letterTerm(l)}`).join("|");
  if (namespaces && namespaces[0] === "Api") {
    // The Api classes no other Api part names by letter.
    return `${term("Api")}&${apiLettersNamed().map((l) => `FullyQualifiedName!~${letterTerm(l)}`).join("&")}`;
  }
  if (namespaces) return namespaces.map(term).join("|");
  const named = [...new Set(PARTS.flatMap((p) => p.namespaces ?? []))];
  return named.map((n) => `FullyQualifiedName!~${ROOT}.${n}.`).join("&");
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const [command, arg] = process.argv.slice(2);
  if (command === "count") console.log(PARTS.length);
  else if (command === "filter") console.log(filterFor(Number(arg)));
  else { console.error("usage: persistence-parts.mjs count | filter <part>"); process.exit(2); }
}
