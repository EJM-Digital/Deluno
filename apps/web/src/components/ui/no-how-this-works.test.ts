import { readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

/**
 * #1430: no screen carries a "How this works" section. Deluno explains itself in a short line, and a setting that
 * needs more gets the shared (i) icon (components/ui/info-hint.tsx). This fails if the words come back in anything
 * the app ships.
 */

const root = path.resolve(__dirname, "../..");
const banned = ["How this", "works"].join(" ");

function sourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const full = path.join(directory, name);
    if (statSync(full).isDirectory()) return name === "node_modules" ? [] : sourceFiles(full);
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) ? [full] : [];
  });
}

describe("no 'How this works' section (#1430)", () => {
  it("is not in any shipped source file", () => {
    const files = sourceFiles(root);
    expect(files.length).toBeGreaterThan(100);
    const offenders = files.filter((file) => readFileSync(file, "utf8").toLowerCase().includes(banned.toLowerCase()));
    expect(offenders.map((file) => path.relative(root, file))).toEqual([]);
  });

  it("has no how-this-works component left to import", () => {
    expect(sourceFiles(root).filter((file) => /how-this-works/i.test(file))).toEqual([]);
  });
});
