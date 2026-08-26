import { describe, expect, it } from "vitest";
import { emptyPlatformSettingsSnapshot, type PlatformSettingsSnapshot } from "./api";
import { describeGlobalSharingRule, describeStrictSharingRule } from "./sharing-rule";

function settings(overrides: Partial<PlatformSettingsSnapshot>): PlatformSettingsSnapshot {
  return { ...emptyPlatformSettingsSnapshot, ...overrides };
}

describe("sharing rule copy", () => {
  // Someone deciding whether a site is "stricter than normal" has to be told
  // what normal is, in the same breath and in the same units they think in.
  it("says the default rule in days, not hours", () => {
    expect(describeGlobalSharingRule(settings({ sharingMode: "share-then-tidy", sharingForHours: 72, sharingUntilRatio: null })))
      .toBe("Your normal rule: keep sharing for 3 days, then reclaim the space.");
  });

  it("names both targets when both are set, because Deluno waits for both", () => {
    expect(describeGlobalSharingRule(settings({ sharingMode: "share-then-tidy", sharingForHours: 24, sharingUntilRatio: 1.5 })))
      .toBe("Your normal rule: keep sharing for 1 day and until ratio 1.5, then reclaim the space.");
  });

  it("covers the two modes that never wait", () => {
    expect(describeGlobalSharingRule(settings({ sharingMode: "tidy-now" }))).toContain("as soon as the import is verified");
    expect(describeGlobalSharingRule(settings({ sharingMode: "leave-alone" }))).toContain("never removes anything");
  });

  // Share-then-tidy with neither target set reclaims immediately, so saying it
  // "keeps sharing" would describe behaviour that never happens.
  it("does not promise sharing when no target is set", () => {
    expect(describeGlobalSharingRule(settings({ sharingMode: "share-then-tidy", sharingForHours: null, sharingUntilRatio: null })))
      .toBe("Your normal rule: reclaim the space as soon as the import is verified.");
  });

  it("states the strict answer as an obligation rather than as settings", () => {
    const copy = describeStrictSharingRule();

    expect(copy).toContain("14 days");
    expect(copy).toContain("given back as much as you took");
    // "ratio 1.0" means nothing to someone joining their first tracker.
    expect(copy).not.toContain("ratio");
  });
});
