import { describe, expect, it } from "vitest";
import { CONFIGURATION_AREAS, findConfigurationArea } from "./configuration-areas";

/**
 * Every tab of every configuration area, written out independently of the
 * matchers so this is a check rather than a restatement. A tab missing here is
 * a tab whose explainer nobody proved shows up.
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
    "/settings/tags"
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

  it.each(Object.entries(AREA_TABS))("shows the %s explainer on every one of its tabs", (id, paths) => {
    for (const path of paths) {
      expect(findConfigurationArea(path)?.id, path).toBe(id);
    }
  });

  it("keeps every explainer inside the shape the pattern allows", () => {
    for (const area of CONFIGURATION_AREAS) {
      expect(area.explainer.lead.length, area.id).toBeGreaterThan(40);
      // Two to four, or none at all. One step is not a sequence and five is a
      // manual — an area whose parts have no order gets a lead and stops.
      expect([0, 2, 3, 4], area.id).toContain(area.explainer.steps.length);
      for (const step of area.explainer.steps) {
        expect(step.title.trim(), area.id).not.toBe("");
        expect(step.body.trim(), area.id).not.toBe("");
        // The toolbar already said where you are.
        expect(step.title.toLowerCase()).not.toBe(area.id.replaceAll("-", " "));
      }
    }
  });

  it("leaves the guided setup alone, which System's sidebar entry claims but its explainer must not", () => {
    expect(findConfigurationArea("/setup-guide")).toBeUndefined();
  });

  it.each(["/", "/movies", "/tv", "/calendar", "/queue", "/activity"])("puts no explainer on %s", (path) => {
    expect(findConfigurationArea(path)).toBeUndefined();
  });
});
