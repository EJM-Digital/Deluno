import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * Deluno has no .nfo or artwork writer, so the Metadata page must not offer
 * switches for them (#735): a switch that is stored and never acted on tells
 * someone files are being written beside their media when none are. When a
 * writer lands, wire it to these settings and delete this test in the same
 * change.
 */
describe("metadata settings page", () => {
  const source = readFileSync(resolve(__dirname, "settings-metadata-page.tsx"), "utf8");

  it("offers no .nfo or artwork switches while Deluno writes neither file", () => {
    expect(source).not.toMatch(/metadataNfoEnabled|metadataArtworkEnabled/);
    expect(source).not.toMatch(/\.nfo\)|Save artwork files/);
  });
});
