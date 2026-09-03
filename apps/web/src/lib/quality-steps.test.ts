import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  QUALITY_STEPS,
  describeAnswer,
  formatsForStep,
  guideCategories,
  orphanedCategories,
  type QualityStep
} from "./quality-steps";
import type { CustomFormatItem, GuidePackage } from "./api";

/**
 * The seven steps are only trustworthy if nothing falls between them. #386's
 * fourth acceptance line is that every field reachable in the six object
 * editors today is reachable from the step that owns it, and the way that line
 * gets broken is quietly: the guide gains a category, no step claims it, and
 * the formats in it simply stop appearing.
 */
describe("the seven steps", () => {
  it("pins quality first and refusal last, because one qualifies and the other subtracts", () => {
    expect(QUALITY_STEPS[0].id).toBe("quality");
    expect(QUALITY_STEPS[QUALITY_STEPS.length - 1].id).toBe("never");
    expect(QUALITY_STEPS.map((step) => step.number)).toEqual([1, 2, 3, 4, 5, 6, 7]);
  });

  it("owns every guide category exactly once", () => {
    expect(orphanedCategories(guide())).toEqual([]);

    const owned = QUALITY_STEPS.flatMap((step) => step.categories);
    expect(owned.length).toBe(new Set(owned).size);
  });

  it("owns every category the shipped guide actually carries", () => {
    // Against the real package, not the fixture below it. A fixture can only
    // fail when somebody remembers to update it, and the failure this guards
    // against is precisely the one nobody remembers: the guide gains a
    // category upstream, no step claims it, and those formats quietly vanish
    // from the only screen that offers them.
    expect(orphanedCategories(shippedGuide())).toEqual([]);

    const claimed = new Set(QUALITY_STEPS.flatMap((step) => step.categories));
    const shipped = new Set(guideCategories(shippedGuide()));
    expect([...claimed].filter((category) => !shipped.has(category))).toEqual([]);
  });

  it("asks questions rather than naming Deluno's own nouns", () => {
    // The failure this replaces was six tabs called Quality Profiles, Size
    // Rules, Release Preferences and Acquisition Rules.
    const nouns = ["profile", "custom format", "preference plan", "acquisition rule", "policy set"];
    for (const step of QUALITY_STEPS) {
      const asked = `${step.question} ${step.purpose}`.toLowerCase();
      for (const noun of nouns) {
        expect(asked, `step ${step.number} says "${noun}"`).not.toContain(noun);
      }
      expect(step.question.endsWith("?")).toBe(true);
    }
  });
});

describe("which formats a step offers", () => {
  it("routes a guide format to the single step that owns its category", () => {
    const picture = step("picture");
    const sound = step("sound");

    expect(formatsForStep(picture, formats, guide()).map((format) => format.name)).toEqual(["HDR10", "x265"]);
    expect(formatsForStep(sound, formats, guide()).map((format) => format.name)).toEqual(["TrueHD Atmos"]);
  });

  it("matches through the guide, so an upstream rename cannot move a format", () => {
    const renamed = guide();
    renamed.customFormats = renamed.customFormats.map((format) =>
      format.trashId === "tid-hdr10" ? { ...format, name: "HDR10 (renamed upstream)" } : format
    );

    expect(formatsForStep(step("picture"), formats, renamed).map((format) => format.id)).toContain("hdr10");
  });

  it("puts a rule you wrote yourself under what you never want", () => {
    // Not a guess about what a local rule means. A rule somebody wrote is a
    // rule they are asserting, and step 7 is the step that shows assertions.
    const mine: CustomFormatItem = { ...formats[0], id: "mine", name: "My own rule", trashId: null };

    expect(formatsForStep(step("never"), [mine], guide()).map((format) => format.id)).toEqual(["mine"]);
    expect(formatsForStep(step("picture"), [mine], guide())).toEqual([]);
  });
});

describe("what a step's answer reads as", () => {
  it("is a sentence rather than a count", () => {
    expect(describeAnswer(step("picture"), ["hdr10"], formats)).toBe("HDR10");
    expect(describeAnswer(step("picture"), ["hdr10", "x265"], formats)).toBe("HDR10 and x265");
  });

  it("says what an unanswered step means, not that it is empty", () => {
    expect(describeAnswer(step("picture"), [], formats)).toBe("No preference — anything is fine");
    expect(describeAnswer(step("never"), [], formats)).toBe("Nothing refused outright");
  });

  it("reads refusal as a refusal", () => {
    expect(describeAnswer(step("never"), ["cam"], formats)).toBe("Never CAM");
  });

  it("stops naming after three and says how many are left", () => {
    const many = ["hdr10", "x265", "truehd", "cam"];
    expect(describeAnswer(step("picture"), many, formats)).toBe("HDR10, x265, and 2 more");
  });

  it("ignores an id that no longer matches a format", () => {
    // A format deleted while a profile still names it must not render as
    // "undefined" on the checklist.
    expect(describeAnswer(step("picture"), ["hdr10", "gone"], formats)).toBe("HDR10");
  });
});

function step(id: QualityStep["id"]): QualityStep {
  const found = QUALITY_STEPS.find((candidate) => candidate.id === id);
  if (!found) throw new Error(`no step ${id}`);
  return found;
}

const formats: CustomFormatItem[] = [
  format("hdr10", "HDR10", "tid-hdr10"),
  format("x265", "x265", "tid-x265"),
  format("truehd", "TrueHD Atmos", "tid-truehd"),
  format("cam", "CAM", "tid-cam")
];

function format(id: string, name: string, trashId: string): CustomFormatItem {
  return {
    id,
    name,
    mediaType: "movies",
    score: 0,
    conditions: "",
    upgradeAllowed: true,
    trashId,
    createdUtc: "2026-09-04T00:00:00Z",
    updatedUtc: "2026-09-04T00:00:00Z"
  };
}

function guide(): GuidePackage {
  return {
    id: "test",
    name: "Test guide",
    version: 1,
    schemaVersion: 1,
    source: {
      sourceName: "TRaSH",
      repositoryUrl: "",
      guideUrl: "",
      upstreamRevision: "",
      reviewedUtc: "",
      adaptation: ""
    },
    integritySha256: "",
    qualityTiers: [],
    qualityProfiles: [],
    bundles: [],
    customFormats: [
      guideFormat("tid-hdr10", "HDR10", "hdr"),
      guideFormat("tid-x265", "x265", "codec"),
      guideFormat("tid-truehd", "TrueHD Atmos", "audio"),
      guideFormat("tid-cam", "CAM", "unwanted"),
      // One of every category the shipped package carries, so the ownership
      // check is against the real vocabulary rather than the four above.
      ...["streaming", "groups", "misc", "edition", "anime", "source", "channels", "language"].map(
        (category, index) => guideFormat(`tid-${category}-${index}`, category, category)
      )
    ]
  } as GuidePackage;
}

function guideFormat(trashId: string, name: string, category: string) {
  return {
    trashId,
    name,
    category,
    description: "",
    originalScore: 0,
    patterns: [],
    bundleOnly: false,
    mappingStatus: "reviewed",
    mappedTraitIds: [],
    sourceKind: "guide"
  } as GuidePackage["customFormats"][number];
}

/** The guide package Deluno ships, read from source rather than mirrored. */
function shippedGuide(): GuidePackage {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const packagePath = path.resolve(here, "../../../../src/Deluno.Quality/Guides/trash-guide-package.json");
  return JSON.parse(readFileSync(packagePath, "utf8")) as GuidePackage;
}
