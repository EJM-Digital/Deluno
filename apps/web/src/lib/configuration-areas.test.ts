import { describe, expect, it } from "vitest";
import { CONFIGURATION_AREAS, findConfigurationArea } from "./configuration-areas";

/**
 * Every tab of every configuration area, written out independently of the
 * matchers so this is a check rather than a restatement. A tab missing here is
 * a tab nobody proved belongs to its area.
 */
const AREA_TABS: Record<string, string[]> = {
  "media-management": [
    "/settings",
    "/settings/libraries",
    "/settings/media-management",
    "/settings/import-policy",
    "/settings/processing",
    "/settings/destination-rules",
    "/settings/metadata",
    "/settings/subtitles",
    "/settings/tags",
    "/settings/auto-tagging"
  ],
  "quality-and-release": ["/settings/profiles", "/settings/quality", "/settings/custom-formats", "/settings/policy-sets"],
  "find-and-download": ["/indexers/indexers", "/indexers/scoreboard", "/indexers/download-clients", "/indexers/library-routing", "/indexers/subtitle-providers"],
  "automation-and-recovery": ["/search-cycles", "/search-cycles/missing", "/search-cycles/upgrades", "/search-cycles/failed-downloads"],
  "discover-media": ["/settings/lists"],
  preferences: ["/settings/general", "/settings/ui", "/settings/notifications", "/settings/migration"],
  system: ["/system", "/system/audit", "/system/backups", "/system/updates", "/system/api", "/system/docs"]
};

describe("configuration areas", () => {
  it("covers every area exactly once", () => {
    expect(CONFIGURATION_AREAS.map((area) => area.id).sort()).toEqual(Object.keys(AREA_TABS).sort());
  });

  it.each(Object.entries(AREA_TABS))("places every %s tab in its own area", (id, paths) => {
    for (const path of paths) {
      expect(findConfigurationArea(path)?.id, path).toBe(id);
    }
  });

  it("leaves the guided setup alone, which System's sidebar entry claims but its explainer must not", () => {
    expect(findConfigurationArea("/setup-guide")).toBeUndefined();
  });

  it.each(["/", "/movies", "/tv", "/calendar", "/queue", "/activity"])("puts %s in no area", (path) => {
    expect(findConfigurationArea(path)).toBeUndefined();
  });
});
