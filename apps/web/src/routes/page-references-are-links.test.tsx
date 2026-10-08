import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

/**
 * #1138: the settings copy that names Transfers or Automation & Recovery,
 * Failed Downloads uses the shared link, not plain words. Reads the source so
 * a new plain-text reference in these two screens fails here.
 */
describe("settings screens link the pages they name", () => {
  for (const file of ["settings-download-health-page.tsx", "settings-media-management-page-v2.tsx"]) {
    it(`${file} has no plain-text page references`, () => {
      const source = readFileSync(join(process.cwd(), "src", "routes", file), "utf8");
      // Plain text means the name sits in the copy rather than in a PageLink.
      expect(source).not.toMatch(/(?:in|on|under) Transfers[ .,"]/);
      const plain = source
        .split(/\r?\n/)
        .filter((line) => line.includes("Automation & Recovery, Failed Downloads") && !line.includes("<PageLink") && !/^\s*(\/\/|\*|\/\*)/.test(line));
      expect(plain).toEqual([]);
      expect(source).toContain("<PageLink");
    });
  }
});
