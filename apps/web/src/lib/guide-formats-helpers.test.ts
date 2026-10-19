import { afterEach, describe, expect, it, vi } from "vitest";
import type { GuideCustomFormat, GuidePackage } from "./api/types";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("./api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));

const { guideFormatAppliesTo, isGenuineGuideFormat, createRuleFromGuide, guideBundleTargets } = await import("./guide-formats");

const format = (over: Partial<GuideCustomFormat> & { trashId: string }) => ({ name: over.trashId, patterns: [], originalScore: 5, mediaTypes: [], ...over }) as unknown as GuideCustomFormat;
afterEach(() => fetchJson.mockReset());

describe("guideFormatAppliesTo", () => {
  it("serves both media types when the format names none", () => {
    expect(guideFormatAppliesTo(format({ trashId: "a" }), "movies")).toBe(true);
    expect(guideFormatAppliesTo(format({ trashId: "a" }), "tv")).toBe(true);
    expect(guideFormatAppliesTo(format({ trashId: "a", mediaTypes: undefined }), "tv")).toBe(true);
  });

  it("serves only the matching one otherwise, treating anything that is not tv as movies", () => {
    const movieOnly = format({ trashId: "m", mediaTypes: ["movies"] });
    const tvOnly = format({ trashId: "t", mediaTypes: ["tv"] });
    expect([guideFormatAppliesTo(movieOnly, "movies"), guideFormatAppliesTo(movieOnly, "tv")]).toEqual([true, false]);
    expect([guideFormatAppliesTo(tvOnly, "movies"), guideFormatAppliesTo(tvOnly, "tv")]).toEqual([false, true]);
    expect(guideFormatAppliesTo(format({ trashId: "x", mediaTypes: ["movie" as never] }), "movies")).toBe(true);
  });
});

describe("isGenuineGuideFormat", () => {
  it("is true only for a format matched by the guide's own clauses with no patterns Deluno wrote", () => {
    const clause = [{ name: "n", implementation: "ReleaseTitleSpecification", negate: false, required: true, fieldsJson: "{}" }] as never;
    expect(isGenuineGuideFormat(format({ trashId: "g", sourceMatcherClauses: clause }))).toBe(true);
    expect(isGenuineGuideFormat(format({ trashId: "g", sourceMatcherClauses: clause, patterns: [{}] as never }))).toBe(false);
    expect(isGenuineGuideFormat(format({ trashId: "g", sourceMatcherClauses: [] }))).toBe(false);
    expect(isGenuineGuideFormat(format({ trashId: "g" }))).toBe(false);
  });
});

describe("createRuleFromGuide", () => {
  it("posts only the guide id, name, media type, score and upgradeAllowed, with no conditions of its own", async () => {
    fetchJson.mockResolvedValue({ id: "rule-1" });
    const result = await createRuleFromGuide(format({ trashId: "abc", name: "AMZN" }), "tv", 120);
    expect(result).toEqual({ id: "rule-1" });
    expect(fetchJson).toHaveBeenCalledTimes(1);
    const [url, init] = fetchJson.mock.calls[0];
    expect(url).toBe("/api/custom-formats");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body)).toEqual({ name: "AMZN", mediaType: "tv", score: 120, trashId: "abc", conditions: null, upgradeAllowed: true });
  });
});

describe("guideBundleTargets", () => {
  const guide = {
    customFormats: [
      format({ trashId: "both", originalScore: 10 }),
      format({ trashId: "movie", mediaTypes: ["movies"], originalScore: 20 }),
      format({ trashId: "tv", mediaTypes: ["tv"], originalScore: 30 })
    ]
  } as unknown as GuidePackage;
  const includes = [{ trashId: "both", score: 100 }, { trashId: "movie" }, { trashId: "tv" }, { trashId: "unknown" }] as never;
  const summary = (scope: string) => guideBundleTargets(includes, scope, guide).map((target) => `${target.format.trashId}:${target.mediaType}:${target.score}`);

  it("a movies bundle adds each format that serves movies", () => {
    expect(summary("movies")).toEqual(["both:movies:100", "movie:movies:20"]);
  });

  it("a tv bundle (and any unrecognised scope) adds each format that serves tv", () => {
    expect(summary("tv")).toEqual(["both:tv:100", "tv:tv:30"]);
    expect(summary("anything")).toEqual(summary("tv"));
  });

  it("an all bundle adds a format to each media type it serves, using the format's own score when the bundle gives none", () => {
    expect(summary("all")).toEqual(["both:movies:100", "both:tv:100", "movie:movies:20", "tv:tv:30"]);
  });

  it("skips an id the guide does not contain", () => {
    expect(summary("all").some((line) => line.startsWith("unknown"))).toBe(false);
  });
});
