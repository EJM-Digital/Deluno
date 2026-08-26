import { describe, expect, it } from "vitest";
import { STRICT_SHARING, sharingRuleFrom } from "../routes/connections/forms";
import { emptyPlatformSettingsSnapshot, type IndexerItem, type PlatformSettingsSnapshot } from "./api";
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

  /**
   * The backend defines this rule in SharingPolicy.Strict and a migration uses
   * it to pre-answer the sharing question for an imported private tracker. This
   * file cannot import C#, so it mirrors the values the same way the settings
   * snapshot mirrors the default rule — and pins them, because the two drifting
   * apart would mean the same answer meant two different things depending on
   * whether you migrated or clicked.
   */
  it("agrees with the backend about what strict means", () => {
    expect(STRICT_SHARING.forHours).toBe(336);
    expect(STRICT_SHARING.untilRatio).toBe(1);
    expect(STRICT_SHARING.stuckAfterDays).toBe(14);
  });

  it("states the strict answer as an obligation rather than as settings", () => {
    const copy = describeStrictSharingRule();

    expect(copy).toContain("14 days");
    expect(copy).toContain("given back as much as you took");
    // "ratio 1.0" means nothing to someone joining their first tracker.
    expect(copy).not.toContain("ratio");
  });
});

describe("reading a source's sharing answer back", () => {
  const base = { id: "a", name: "A tracker" } as IndexerItem;

  /**
   * The indexers list shows this instead of the old "Private"/"Public" label,
   * which nobody could set and Deluno never read. It has to be quiet for the
   * ordinary case: a row saying "normal rule" on every source is noise.
   */
  it("only reports a source that differs from the normal rule", () => {
    expect(sharingRuleFrom(base)).toBe("inherit");
    expect(sharingRuleFrom({ ...base, sharingMode: "share-then-tidy" })).toBe("strict");
    expect(sharingRuleFrom({ ...base, sharingUntilRatio: 1 })).toBe("strict");
    expect(sharingRuleFrom({ ...base, sharingForHours: 336 })).toBe("strict");
  });

  /**
   * A migrated private tracker arrives already carrying the strict rule, so the
   * list has to read it back as strict without anyone having touched the form.
   */
  it("reads a migrated private tracker as strict", () => {
    expect(
      sharingRuleFrom({
        ...base,
        sharingMode: "share-then-tidy",
        sharingForHours: STRICT_SHARING.forHours,
        sharingUntilRatio: STRICT_SHARING.untilRatio,
        sharingStuckAction: "keep-waiting",
        sharingStuckAfterDays: STRICT_SHARING.stuckAfterDays
      })
    ).toBe("strict");
  });
});
