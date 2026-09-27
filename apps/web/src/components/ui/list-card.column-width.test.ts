import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * ListTable lays each row out as its own CSS grid (see list-card.tsx). A
 * column sized `width: "auto"` is therefore as wide as THAT row's own
 * content — a row with no button (or a shorter one) collapses the column
 * and every column after it drifts out of line with the rows around it
 * (#935, first seen on System → System tasks). `LIST_TRACK.action` is the
 * fixed track that keeps a trailing button column the same width on every
 * row, whatever that row's buttons are.
 *
 * This scans every source file for a `ListTable` column literally sized
 * `width: "auto"` (or `'auto'`) and fails the build if one appears — the
 * same way the System tasks table, and every consumer fixed for #935, went
 * wrong. Fix it with `LIST_TRACK.action` (or another fixed/`minmax` track),
 * never a bare `"auto"`.
 */

const SRC_ROOT = resolve(__dirname, "../../");
const BARE_AUTO_WIDTH = /width:\s*["']auto["']/;

function collectSourceFiles(dir: string, files: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === "node_modules") continue;
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      collectSourceFiles(path, files);
    } else if (/\.(tsx?|jsx?)$/.test(entry.name) && !/\.test\.[jt]sx?$/.test(entry.name)) {
      files.push(path);
    }
  }
  return files;
}

describe("ListTable column widths", () => {
  it("never sizes a column with a bare \"auto\" width", () => {
    const offenders = collectSourceFiles(SRC_ROOT)
      .filter((file) => BARE_AUTO_WIDTH.test(readFileSync(file, "utf8")))
      .map((file) => file.replace(SRC_ROOT, "src").replace(/\\/g, "/"));

    expect(offenders).toEqual([]);
  });
});
