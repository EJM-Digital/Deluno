import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { metadataKeysUnreadable, metadataPreferencesIgnored } from "./settings-metadata-page";
import type { MetadataProviderStatus } from "../lib/api/types/metadata";

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

describe("metadata language and region note (#736)", () => {
  const status = (mode: string) => ({ provider: "x", isConfigured: true, mode, message: "", sources: [] }) as MetadataProviderStatus;

  it("says the settings have no effect when the managed service answers lookups", () => {
    expect(metadataPreferencesIgnored(status("broker"))).toBe(true);
    expect(metadataPreferencesIgnored(status("hybrid"))).toBe(true);
  });

  it("says nothing when Deluno talks to TMDb directly, or when the mode is unknown", () => {
    expect(metadataPreferencesIgnored(status("direct"))).toBe(false);
    expect(metadataPreferencesIgnored(status("direct-fallback"))).toBe(false);
    expect(metadataPreferencesIgnored(null)).toBe(false);
  });
});

describe("metadata keys saved on another computer (#1094)", () => {
  it("asks for the key again only when one is unreadable here", () => {
    expect(metadataKeysUnreadable({})).toBe(false);
    expect(metadataKeysUnreadable({ metadataTmdbApiKeyUnreadable: true })).toBe(true);
    expect(metadataKeysUnreadable({ mdbListApiKeyUnreadable: true })).toBe(true);
  });

  it("shows the plain sentence and a way to enter the key again", () => {
    const source = readFileSync(resolve(__dirname, "settings-metadata-page.tsx"), "utf8");
    expect(source).toContain("SecretUnreadableNotice");
    expect(source).toContain("Save keys");
  });
});
