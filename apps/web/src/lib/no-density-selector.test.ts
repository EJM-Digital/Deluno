import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { extname, resolve } from "node:path";

/**
 * #908 removed the Standard/Compact/Spacious/Expanded display-density switch:
 * one set of layout tokens now scales with the viewport instead of a stored
 * choice. This walks the whole web source tree rather than a single file
 * because the switch had five points of entry — the top-bar menu, the
 * Settings -> Interface field, the `DensityProvider`/`useDensity` context,
 * the density-keyed props threaded through the library grid, and the
 * `html[data-density]` CSS blocks — and a regression in any one of them is a
 * selector coming back by another name.
 *
 * `.density-help` / `.density-label` / `.density-field` / `.density-control-text`
 * / `.density-nowrap` are not the switch: they are pre-existing utility class
 * names for scaled typography that happen to start with "density" and are
 * unaffected by this change, so the patterns below are specific to the
 * removed selector rather than to the word "density".
 */
const SRC_ROOT = resolve(__dirname, "..");

const DENSITY_SELECTOR_PATTERNS = [
  /data-density/i,
  /useDensity/,
  /DensityProvider/,
  /\buiDensity\b/,
  /\bUiDensity\b/,
  /Display density/i,
  /--density-scale/
];

function collectSourceFiles(dir: string): string[] {
  const entries = readdirSync(dir);
  const files: string[] = [];

  for (const entry of entries) {
    if (entry === "node_modules" || entry === "dist") continue;
    const fullPath = resolve(dir, entry);
    const stats = statSync(fullPath);

    if (stats.isDirectory()) {
      files.push(...collectSourceFiles(fullPath));
      continue;
    }

    if ([".ts", ".tsx", ".css"].includes(extname(entry))) {
      files.push(fullPath);
    }
  }

  return files;
}

describe("no density selector remains (#908)", () => {
  it("finds no data-density, useDensity/DensityProvider, uiDensity, or --density-scale reference anywhere in apps/web/src", () => {
    const offenders: string[] = [];

    for (const file of collectSourceFiles(SRC_ROOT)) {
      // This test's own file names the removed patterns in prose, so it
      // would otherwise flag itself.
      if (file === resolve(__dirname, "no-density-selector.test.ts")) continue;

      const source = readFileSync(file, "utf8");
      if (DENSITY_SELECTOR_PATTERNS.some((pattern) => pattern.test(source))) {
        offenders.push(file);
      }
    }

    expect(offenders).toEqual([]);
  });
});
