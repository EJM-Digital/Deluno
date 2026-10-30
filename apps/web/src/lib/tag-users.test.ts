import { describe, expect, it } from "vitest";
import type { TagUsageItem } from "./api";
import { describeTagInUse, describeTagUsage, tagUserKindLabel, unusedTags } from "./tag-users";

const KINDS: Array<[string, string, string, string]> = [
  ["indexer", "Indexer", "indexer", "indexers"],
  ["downloadClient", "Download client", "download client", "download clients"],
  ["notification", "Notification", "notification", "notifications"],
  ["sharingRule", "Seeding rule", "seeding rule", "seeding rules"],
  ["librarySource", "Library source", "library source rule", "library source rules"],
  ["acquisitionRule", "Release rule", "release rule", "release rules"],
  ["destinationRule", "Destination rule", "destination rule", "destination rules"],
  ["seerrConnection", "Seerr connection", "Seerr connection", "Seerr connections"],
  ["list", "List", "list", "lists"],
  ["collection", "Collection", "collection", "collections"],
  ["listSetting", "List setting", "list setting", "list settings"],
  ["autoTagRule", "Auto-tagging rule", "auto-tagging rule", "auto-tagging rules"],
  ["retentionRule", "Retention rule", "retention rule", "retention rules"]
];

const usage = (kinds: string[], totalCount = 0): TagUsageItem => ({
  id: "t1", name: "kids", totalCount, isUnused: false, users: kinds.map((kind, index) => ({ kind, id: `u${index}`, name: `User ${index}` }))
}) as unknown as TagUsageItem;

describe("what uses a tag, in plain words (low rows)", () => {
  it.each(KINDS)("the kind %s is labelled %s", (kind, label) => {
    expect(tagUserKindLabel(kind)).toBe(label);
  });

  it.each(KINDS)("one %s reads '1 %s' and two read '2 %s'", (kind, _label, one, many) => {
    expect(describeTagInUse(usage([kind]))).toBe(`1 ${one}`);
    expect(describeTagInUse(usage([kind, kind]))).toBe(`2 ${many}`);
  });

  it("an unknown kind is a setting, and titles come first", () => {
    expect(tagUserKindLabel("somethingNew")).toBe("Setting");
    expect(describeTagInUse(usage(["somethingNew"], 3))).toBe("3 titles and 1 setting");
    expect(describeTagInUse(usage(["indexer", "downloadClient"], 1))).toBe("1 title, 1 indexer and 1 download client");
  });
});

describe("what a tag is used for, in a short line", () => {
  const tag = (overrides: Partial<TagUsageItem>): TagUsageItem => ({ ...usage([]), ...overrides }) as TagUsageItem;

  it("says nothing when nothing uses the tag", () => {
    expect(describeTagInUse(usage([]))).toBe("nothing");
  });

  it("says Not used for a tag nobody uses, or one with no usage record", () => {
    expect(describeTagUsage(undefined)).toBe("Not used");
    expect(describeTagUsage(tag({ isUnused: true }))).toBe("Not used");
  });

  it("counts titles and settings, leaving out whichever is zero", () => {
    expect(describeTagUsage(usage(["indexer", "list"], 3))).toBe("3 titles, 2 settings");
    expect(describeTagUsage(usage([], 1))).toBe("1 title");
    expect(describeTagUsage(usage(["indexer"], 0))).toBe("1 setting");
  });

  it("picks out the tags nobody uses", () => {
    const tags = [{ id: "a" }, { id: "b" }];
    expect(unusedTags(tags, [tag({ id: "a", isUnused: true }), tag({ id: "b", isUnused: false })])).toEqual([{ id: "a" }]);
  });
});
