#!/usr/bin/env node

/**
 * Guards the rule that no screen refreshes its data on a timer (#1179).
 *
 * The whole app is live: the server pushes every change, and a page that lost its connection says
 * so and catches up once when it returns. A `refetchInterval`, a `setInterval`, a `useLiveInterval`
 * or a `subscribeTick` in a screen is a poll, and a poll goes stale the moment someone turns its
 * cadence off. This fails the build when one appears in apps/web/src.
 *
 * A timer that only moves a clock on screen ("3 min ago") or keeps the connection itself alive is
 * allowed when the line, or the line above it, carries a comment starting `display-clock:` that
 * says what it does. Code comments and strings are otherwise ignored.
 */
import { readFileSync, readdirSync, statSync } from "fs";
import { dirname, join, relative } from "path";
import { fileURLToPath } from "url";

const here = dirname(fileURLToPath(import.meta.url));
const sourceRoot = join(here, "..", "src");
const sourceExtensions = new Set([".ts", ".tsx"]);

const PATTERNS: { name: string; pattern: RegExp }[] = [
  { name: "refetchInterval", pattern: /\brefetchInterval(?:InBackground)?\b/ },
  { name: "setInterval", pattern: /\bsetInterval\s*\(/ },
  { name: "useLiveInterval", pattern: /\buseLiveInterval\s*\(/ },
  { name: "subscribeTick", pattern: /\bsubscribeTick\s*\(/ },
  { name: "usePollInterval", pattern: /\busePollInterval\b/ }
];

const MARKER = /display-clock:/;

// The shared ticker itself, and the hook that wraps it: the definitions, not uses.
const DEFINITIONS = new Set(["lib/live-ticker.ts", "workers/live-ticker.worker.ts", "hooks/use-live-interval.ts"]);


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
    const name = relative(sourceRoot, filePath).split("\\").join("/");
    if (DEFINITIONS.has(name)) continue;
    check(filePath, name);
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
    // Strip block comments that open and close on the line, then one that opens and stays open.
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
    }
  });
}

visit(sourceRoot);

if (failures.length > 0) {
  console.error("A screen refreshes on a timer. The server pushes every change; subscribe to the event instead.");
  console.error("(A clock on screen is allowed with a `display-clock:` comment saying what it does.)\n");
  for (const failure of failures) console.error(`  ${failure}`);
  console.error(`\n${failures.length} timer${failures.length === 1 ? "" : "s"} found.`);
  process.exit(1);
}

console.log("No screen refreshes on a timer.");
