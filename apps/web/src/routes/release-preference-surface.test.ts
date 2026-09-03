import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const detailSources = [
  "src/routes/movie-detail-page.tsx",
  "src/routes/show-detail-page.tsx"
].map((file) => readFileSync(resolve(process.cwd(), file), "utf8"));
const profileSource = readFileSync(resolve(process.cwd(), "src/routes/settings-profiles-page.tsx"), "utf8");
const customFormatSource = readFileSync(resolve(process.cwd(), "src/routes/settings-custom-formats-page.tsx"), "utf8");

describe("release preference owner surfaces", () => {
  it("never presents the legacy aggregate score as a primary decision fact", () => {
    for (const source of detailSources) {
      expect(source).not.toContain("Legacy score");
      expect(source).not.toContain("legacy input");
      expect(source).toContain("Legacy compatibility rules");
    }
  });

  it("keeps plan explanations and release testing on the typed contract", () => {
    expect(profileSource).toContain("Effective release preferences");
    expect(profileSource).toContain("Advanced review");
    expect(customFormatSource).toContain("previewReleasePreference");
    expect(customFormatSource).toContain("Typed plan preview");
    expect(customFormatSource).not.toContain("typed plan score");
  });

  it("shows what a guide update would change, rather than how many things it would change", () => {
    // #350 asks for a readable plan diff. The diff was computed and then
    // reported as "N guide profile(s) have a compiled-package diff", which is
    // the number of readable diffs that were not shown.
    expect(customFormatSource).toContain("Guide profile plan diff");
    expect(customFormatSource).toContain("diff.changes.map");
    expect(customFormatSource).not.toContain("have a compiled-package diff");
  });

  it("offers the way back to a retained guide version", () => {
    // Every guide version is immutable and kept, which is what makes an
    // update a rollback point. A point with no way back is not one.
    expect(customFormatSource).toContain("Guide version history");
    expect(customFormatSource).toContain("Go back to this version");
    expect(customFormatSource).toContain("activateTrashGuideVersion");
  });
});
