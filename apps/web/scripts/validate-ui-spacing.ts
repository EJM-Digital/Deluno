#!/usr/bin/env node

/**
 * Guards Deluno's visual rhythm. Large fixed Tailwind stack/grid utilities
 * make pages drift apart as new screens are added, so application layout must
 * use the shared density-aware tokens instead.
 *
 * Small 4/8/12px control spacing remains valid for labels, icon rows and
 * compact field groups. This check deliberately covers only macro layout
 * values (16px and above).
 */
import { readFileSync, readdirSync, statSync } from "fs";
import { dirname, join, relative } from "path";
import { fileURLToPath } from "url";

const here = dirname(fileURLToPath(import.meta.url));
const sourceRoot = join(here, "..", "src");
const layoutUtility = /(?<![\w-])(?:(?:space-y|gap)-(?:4|5|6|8|10|12))(?![\w-])/g;
const sourceExtensions = new Set([".ts", ".tsx"]);
const failures: string[] = [];

function visit(directory: string) {
  for (const entry of readdirSync(directory)) {
    const filePath = join(directory, entry);
    const stat = statSync(filePath);
    if (stat.isDirectory()) {
      visit(filePath);
      continue;
    }
    if (!sourceExtensions.has(filePath.slice(filePath.lastIndexOf(".")))) continue;

    const lines = readFileSync(filePath, "utf8").split(/\r?\n/);
    lines.forEach((line, index) => {
      layoutUtility.lastIndex = 0;
      const matches = line.match(layoutUtility);
      if (!matches) return;
      failures.push(`${relative(sourceRoot, filePath)}:${index + 1} uses ${matches.join(", ")}`);
    });
  }
}

visit(sourceRoot);

if (failures.length) {
  console.error("Deluno UI spacing check failed. Use --page-gap or --grid-gap instead of fixed macro layout utilities:");
  failures.forEach((failure) => console.error(`  ${failure}`));
  process.exit(1);
}

console.log("UI spacing check passed: all macro stacks and grids use shared tokens.");
