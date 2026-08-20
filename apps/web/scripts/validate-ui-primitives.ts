#!/usr/bin/env node

import { readFileSync, readdirSync, statSync } from "fs";
import { dirname, join, relative } from "path";
import { fileURLToPath } from "url";

const here = dirname(fileURLToPath(import.meta.url));
const sourceRoot = join(here, "..", "src");
const primitivesRoot = join(sourceRoot, "components", "ui");
const sourceExtensions = new Set([".ts", ".tsx"]);
const rules = [
  { pattern: /<select\b/, message: "use Select from components/ui/select" },
  { pattern: /type=["']checkbox["']/, message: "use Checkbox or Switch from components/ui" },
  { pattern: /role=["']switch["']/, message: "use Switch from components/ui/switch" },
  { pattern: /<textarea\b/, message: "use Textarea from components/ui/textarea" }
];
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
    if (filePath.startsWith(primitivesRoot)) continue;

    readFileSync(filePath, "utf8").split(/\r?\n/).forEach((line, index) => {
      for (const rule of rules) {
        if (rule.pattern.test(line)) failures.push(`${relative(sourceRoot, filePath)}:${index + 1} ${rule.message}`);
      }
    });
  }
}

visit(sourceRoot);

if (failures.length) {
  console.error("Deluno UI primitive check failed:");
  failures.forEach((failure) => console.error(`  ${failure}`));
  process.exit(1);
}

console.log("UI primitive check passed: every form control comes from components/ui.");
