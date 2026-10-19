#!/usr/bin/env node
// The repository's owner is written in exactly one place per language (repository.json for scripts,
// src/Deluno.Contracts/ProjectRepositories.cs for C#). Anywhere else in the shipped code, scripts, workflows and
// build files, writing the owner by hand means the move to a new owner (docs/exec-plans/active/PLAN-ORG-MOVE.md)
// misses it. This fails on the old owner's name, case-insensitively, in src/, apps/, installer/, scripts/, .github/,
// Dockerfile*, docker-compose*, *.props and the *.json files at the root. docs/ and *.md are out of scope until the
// flip, and so is tests/ (recorded replies are real replies from the old address and stay as recorded).
//
// Usage: node scripts/check-repository-references.mjs
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

// Spelled in two halves so this file does not trip its own check.
export const OLD_OWNER = "jampat" + "000";

/** The single-value files: the only places the old owner may be written. */
export const DECLARED = ["repository.json", "src/Deluno.Contracts/ProjectRepositories.cs"];

/**
 * Files that still point at Deluno's current repository on purpose until the flip, each listed in PLAN-ORG-MOVE.md.
 * Remove an entry here when the flip changes the file.
 */
export const PENDING_FLIP = [".github/ISSUE_TEMPLATE/config.yml"];

const SCANNED_DIRECTORIES = ["src", "apps", "installer", "scripts", ".github"];
const SKIPPED_DIRECTORY_NAMES = new Set(["node_modules", "bin", "obj", "dist", ".git", ".vs", "coverage"]);
const BINARY = /\.(png|jpe?g|gif|ico|woff2?|ttf|eot|exe|dll|zip|nupkg|pdf|db|sqlite)$/i;

function isRootFileInScope(name) {
  return /^Dockerfile/i.test(name) || /^docker-compose/i.test(name) || /\.props$/i.test(name) || /\.json$/i.test(name);
}

function* walk(root, relative) {
  const full = path.join(root, relative);
  for (const entry of readdirSync(full, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIPPED_DIRECTORY_NAMES.has(entry.name)) continue;
      yield* walk(root, path.posix.join(relative, entry.name));
    } else if (entry.isFile() && !BINARY.test(entry.name) && !entry.name.endsWith(".md")) {
      yield path.posix.join(relative, entry.name);
    }
  }
}

export function findViolations(root, { declared = DECLARED, pending = PENDING_FLIP } = {}) {
  const allowed = new Set([...declared, ...pending]);
  const files = [];
  for (const dir of SCANNED_DIRECTORIES) {
    if (existsSync(path.join(root, dir))) files.push(...walk(root, dir));
  }
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (entry.isFile() && isRootFileInScope(entry.name)) files.push(entry.name);
  }

  const violations = [];
  for (const file of files) {
    if (allowed.has(file)) continue;
    if (statSync(path.join(root, file)).size > 5_000_000) continue;
    const lines = readFileSync(path.join(root, file), "utf8").split(/\r?\n/);
    lines.forEach((line, index) => {
      if (line.toLowerCase().includes(OLD_OWNER)) violations.push({ file, line: index + 1, text: line.trim().slice(0, 140) });
    });
  }
  return violations;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const violations = findViolations(root);
  if (violations.length > 0) {
    console.error(`The old owner name is written outside the single-value files (${DECLARED.join(", ")}):`);
    for (const v of violations) console.error(`  ${v.file}:${v.line}: ${v.text}`);
    console.error("Read the repository from repository.json / ProjectRepositories, or write a bare issue number in comments.");
    process.exit(1);
  }
  console.log("Repository references OK.");
}
