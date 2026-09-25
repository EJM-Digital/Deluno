import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * The global "Remove empty folders after import" switch (CleanupEmptyFolders)
 * was stored and never read: import only ever consulted the per-library
 * LibraryItem.RemoveEmptySourceFolders switch (#736). If this switch comes
 * back on this page without a reader wired up, it is dead again.
 */
describe("import policy page", () => {
  const source = readFileSync(resolve(__dirname, "settings-media-management-page-v2.tsx"), "utf8");

  it("offers no global empty-folder cleanup switch", () => {
    expect(source).not.toMatch(/cleanupEmptyFolders/);
    expect(source).not.toMatch(/Remove empty folders after import/);
  });

  it("never sends cleanupEmptyFolders in its settings patches", () => {
    expect(source).not.toMatch(/cleanupEmptyFolders:/);
  });

  it("says empty-folder cleanup is a per-library choice instead", () => {
    expect(source).toMatch(/set per library/);
  });
});
