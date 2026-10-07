#!/usr/bin/env node

/**
 * Guards the words on screen (#1138).
 *
 * Shown copy never carries an issue number, never tells a fresh install that
 * something "moved", and only names a page by its current sidebar name. Code
 * comments are ignored; only strings and JSX text are checked.
 *
 * A line that has to name something outside Deluno (another product's own
 * menu, say) goes in ALLOWED with the reason, so a new one is visible in review.
 */
import { readFileSync, readdirSync, statSync } from "fs";
import { dirname, join, relative } from "path";
import { fileURLToPath } from "url";

const here = dirname(fileURLToPath(import.meta.url));
const sourceRoot = join(here, "..", "src");
const sourceExtensions = new Set([".ts", ".tsx"]);

const RULES: { name: string; pattern: RegExp; hint: string }[] = [
  { name: "issue number", pattern: /(?<![\w&])#\d{3,4}\b/, hint: "never show an issue number" },
  { name: "old page name", pattern: /Search (?:&|&amp;|and) Cycles/, hint: "that page is now Automation & Recovery" },
  { name: "old page name", pattern: /Settings,\s*General/, hint: "there is no page by that name; use Preferences or System" },
  { name: "old page path", pattern: /\bSettings\s*(?:->|→|-&gt;|&rarr;)/, hint: "name the page and tab as the sidebar does" },
  { name: "old page name", pattern: /\b(?:on|in|from|to|the) (?:the )?Queue\b(?! (?:import|replacement|item))/, hint: "that page is now Transfers" },
  { name: "old page name", pattern: /\bQueue(?: screen|'s)/, hint: "that page is now Transfers" },
  { name: "change note", pattern: /\b(?:moved|now lives?|now live) (?:to|on|in) (?:its own|a new|the new) (?:page|tab|screen)\b/, hint: "a fresh install has nothing that moved" },
  { name: "other product named", pattern: /\b(?:Sonarr|Radarr|FileFlows|Cleanarr)\b/, hint: "describe the choice, not another product" }
];

// path (relative to src, forward slashes) -> reason these files may name other products.
const ALLOWED_FILES = new Map([
  ["routes/settings-migration-page.tsx", "the importer names the apps it reads from"],
  ["lib/api.ts", "wire values and provider ids"]
]);

// Lines that must name another product's own menu, or a provider id.
const ALLOWED_LINES: RegExp[] = [
  /Settings → General → API Key in Seerr/,
  /Settings → Advanced → API Keys/,
  /fileflows-webhook/
];

const failures: string[] = [];

function stripComments(text: string): string {
  const blank = (match: string) => match.replace(/[^\n]/g, "");
  return text
    .replace(/\/\*[\s\S]*?\*\//g, blank)
    .split(/\r?\n/)
    .map((line) => line.replace(/(^|\s)\/\/.*$/, "$1"))
    .join("\n");
}

function visit(directory: string) {
  for (const entry of readdirSync(directory)) {
    const filePath = join(directory, entry);
    if (statSync(filePath).isDirectory()) {
      visit(filePath);
      continue;
    }
    if (!sourceExtensions.has(filePath.slice(filePath.lastIndexOf(".")))) continue;
    if (/\.(?:test|spec)\.tsx?$/.test(filePath)) continue;

    const relativePath = relative(sourceRoot, filePath).replaceAll("\\", "/");
    if (relativePath.startsWith("test/") || ALLOWED_FILES.has(relativePath)) continue;

    stripComments(readFileSync(filePath, "utf8")).split(/\r?\n/).forEach((line, index) => {
      if (ALLOWED_LINES.some((allowed) => allowed.test(line))) return;
      for (const rule of RULES) {
        if (rule.pattern.test(line)) {
          failures.push(`${relativePath}:${index + 1} ${rule.name} (${rule.hint}): ${line.trim().slice(0, 110)}`);
        }
      }
    });
  }
}

visit(sourceRoot);

if (failures.length) {
  console.error("UI copy check failed. Shown copy must be plain words and current page names:");
  failures.forEach((failure) => console.error(`  ${failure}`));
  process.exit(1);
}

console.log("UI copy check passed: no issue numbers, old page names or change notes in shown copy.");
