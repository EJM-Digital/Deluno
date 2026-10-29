#!/usr/bin/env node

/**
 * Guards the rule that no screen builds a counted phrase by hand (#1423).
 *
 * `${n} title${n === 1 ? "" : "s"}` is how "2 movie librarys" got on screen: the guess at the plural was
 * made again at every site, and a word ending in y gets it wrong. Every counted thing goes through the one
 * helper, `plural(count, "movie library")` in lib/plural.ts, which writes "1 movie library" and "2 movie
 * libraries" and takes the plural itself when the words change with the count.
 *
 * This fails the build when apps/web/src chooses "s", "es" or "ies" (or a word and the same word with one
 * of them added) by comparing a number. Code comments are ignored; a line that must keep its own wording
 * carries a comment starting `plural-ok:` that says why.
 */
import { readFileSync, readdirSync, statSync } from "fs";
import { dirname, join, relative } from "path";
import { fileURLToPath } from "url";

const here = dirname(fileURLToPath(import.meta.url));
// A folder can be named on the command line, so the check itself can be tested against sample files.
const sourceRoot = process.argv[2] ?? join(here, "..", "src");
const sourceExtensions = new Set([".ts", ".tsx"]);

const NUMBER_TEST = String.raw`(?:[=!]==?|[<>]=?)\s*-?\d+(?:\.\d+)?\s*\?\s*`;
const QUOTE = String.raw`["'` + "`" + String.raw`]`;
const PATTERNS: { name: string; pattern: RegExp }[] = [
  // `n === 1 ? "" : "s"`, `n !== 1 ? "s" : ""`, `n > 1 ? "es" : ""`
  { name: "an s added by comparing a number", pattern: new RegExp(NUMBER_TEST + `(?:${QUOTE}${QUOTE}\\s*:\\s*${QUOTE}(?:s|es|ies)${QUOTE}|${QUOTE}(?:s|es|ies)${QUOTE}\\s*:\\s*${QUOTE}${QUOTE})`) },
  // `n === 1 ? "y" : "ies"`
  { name: "y or ies chosen by comparing a number", pattern: new RegExp(NUMBER_TEST + `${QUOTE}y${QUOTE}\\s*:\\s*${QUOTE}ies${QUOTE}`) },
  // `n === 1 ? "title" : "titles"`, `n === 1 ? "match" : "matches"`, `n === 1 ? "library" : "libraries"`
  { name: "a word and its plural chosen by comparing a number", pattern: new RegExp(NUMBER_TEST + `${QUOTE}([A-Za-z ]+?)${QUOTE}\\s*:\\s*${QUOTE}\\1(?:s|es)${QUOTE}`) },
  { name: "a word and its plural chosen by comparing a number", pattern: new RegExp(NUMBER_TEST + `${QUOTE}([A-Za-z ]*[^aeiou])y${QUOTE}\\s*:\\s*${QUOTE}\\1ies${QUOTE}`) },
  // `n !== 1 && "s"`
  { name: "an s added with &&", pattern: new RegExp(String.raw`[=!<>]=?\s*\d+\s*&&\s*` + QUOTE + String.raw`s` + QUOTE) }
];

const MARKER = /plural-ok:/;

const failures: string[] = [];

function visit(directory: string) {
  for (const entry of readdirSync(directory)) {
    const filePath = join(directory, entry);
    if (statSync(filePath).isDirectory()) {
      visit(filePath);
      continue;
    }
    const extension = entry.slice(entry.lastIndexOf("."));
    if (!sourceExtensions.has(extension) || /\.test\.tsx?$/.test(entry)) continue;
    check(filePath, relative(sourceRoot, filePath).split("\\").join("/"));
  }
}

function check(filePath: string, name: string) {
  const lines = readFileSync(filePath, "utf8").split(/\r?\n/);
  let inBlockComment = false;
  lines.forEach((raw, index) => {
    let line = raw;
    if (inBlockComment) {
      const end = line.indexOf("*/");
      if (end < 0) return;
      line = line.slice(end + 2);
      inBlockComment = false;
    }
    line = line.replace(/\/\*.*?\*\//g, "");
    const open = line.indexOf("/*");
    if (open >= 0) {
      line = line.slice(0, open);
      inBlockComment = true;
    }
    line = line.replace(/(^|\s)\/\/.*$/, "$1");
    for (const { name: kind, pattern } of PATTERNS) {
      if (!pattern.test(line)) continue;
      const here = MARKER.test(raw);
      const above = index > 0 && MARKER.test(lines[index - 1] ?? "");
      if (here || above) continue;
      failures.push(`${name}:${index + 1}  ${kind}  ${raw.trim()}`);
      break;
    }
  });
}

visit(sourceRoot);

if (failures.length > 0) {
  console.error("A screen builds a counted phrase by hand. Write it with plural(count, \"word\") from lib/plural.ts.");
  console.error("(A line that must keep its own wording may say why in a `plural-ok:` comment.)\n");
  for (const failure of failures) console.error(`  ${failure}`);
  console.error(`\n${failures.length} found.`);
  process.exit(1);
}

console.log("Every counted phrase goes through plural().");
