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
});
