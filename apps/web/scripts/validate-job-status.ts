#!/usr/bin/env node

/**
 * Validation script to detect hardcoded job status strings in the frontend.
 * This ensures all job status comparisons use the shared JOB_STATUS constants.
 *
 * Usage: npx ts-node scripts/validate-job-status.ts
 */

import { readFileSync, readdirSync, statSync } from "fs";
import { join, dirname } from "path";
import { fileURLToPath } from "url";

const __dirname = dirname(fileURLToPath(import.meta.url));

// Job status strings that must not be hardcoded
const FORBIDDEN_PATTERNS = [
  /"queued"/g,
  /'queued'/g,
  /"running"/g,
  /'running'/g,
  /"completed"/g,
  /'completed'/g,
  /"failed"/g,
  /'failed'/g
];

// File patterns to check
const INCLUDE_PATTERNS = [/\.(tsx?|jsx?)$/];
const EXCLUDE_PATTERNS = [
  /node_modules/,
  /dist/,
  /build/,
  /\.next/,
  /job-status-constants\.ts$/
];

interface ValidationError {
  file: string;
  line: number;
  match: string;
}

const errors: ValidationError[] = [];

function shouldCheckFile(filePath: string): boolean {
  const relativePath = filePath.replace(/\\/g, "/");

  if (EXCLUDE_PATTERNS.some(p => p.test(relativePath))) {
    return false;
  }

  return INCLUDE_PATTERNS.some(p => p.test(filePath));
}

function validateFile(filePath: string): void {
  try {
    const content = readFileSync(filePath, "utf-8");
    const lines = content.split("\n");

    lines.forEach((line, index) => {
      // Skip comments and strings that are clearly part of imports
      if (line.includes("job-status-constants") || line.trim().startsWith("//")) {
        return;
      }

      // Skip lines that use JOB_STATUS constant correctly
      if (line.includes("JOB_STATUS.") || line.includes("isJobActive") || line.includes("isJobInProgress") || line.includes("isJobDone")) {
        return;
      }

      FORBIDDEN_PATTERNS.forEach(pattern => {
        const matches = line.match(pattern);
        if (matches) {
          matches.forEach(match => {
            errors.push({
              file: filePath,
              line: index + 1,
              match
            });
          });
        }
      });
    });
  } catch (error) {
    console.error(`Error reading file ${filePath}:`, error);
  }
}

function walkDirectory(dir: string): void {
  try {
    const entries = readdirSync(dir);

    entries.forEach(entry => {
      const fullPath = join(dir, entry);
      const stat = statSync(fullPath);

      if (stat.isDirectory()) {
        walkDirectory(fullPath);
      } else if (shouldCheckFile(fullPath)) {
        validateFile(fullPath);
      }
    });
  } catch (error) {
    console.error(`Error reading directory ${dir}:`, error);
  }
}

// Main execution
const srcDir = join(__dirname, "../src");
walkDirectory(srcDir);

if (errors.length > 0) {
  console.error(`\n❌ Found ${errors.length} hardcoded job status string(s):\n`);

  const groupedByFile: Record<string, ValidationError[]> = {};
  errors.forEach(error => {
    if (!groupedByFile[error.file]) {
      groupedByFile[error.file] = [];
    }
    groupedByFile[error.file].push(error);
  });

  Object.entries(groupedByFile).forEach(([file, fileErrors]) => {
    console.error(`\n${file}:`);
    fileErrors.forEach(error => {
      console.error(`  Line ${error.line}: Found ${error.match}`);
    });
  });

  console.error("\n💡 Use JOB_STATUS constants instead:");
  console.error("   import { JOB_STATUS, isJobActive } from '../lib/job-status-constants';");
  console.error("   if (isJobActive(status)) { ... }");

  process.exit(1);
} else {
  console.log("✅ No hardcoded job status strings found!");
  process.exit(0);
}
