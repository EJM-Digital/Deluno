import { describe, expect, it } from "vitest";
import { describeMatchedCondition, formatRange, parseRange, slugForFileName } from "./settings-custom-formats-page";

describe("describeMatchedCondition", () => {
  it("names the parsed language that matched, not a raw text location (#758)", () => {
    expect(describeMatchedCondition("language:French")).toBe("Language matched in the release name: French");
  });

  it("negates a language match in plain words", () => {
    expect(describeMatchedCondition("NOT language:French")).toBe("Not language matched in the release name: French");
  });

  it("falls back to the friendly condition-type label for other types", () => {
    expect(describeMatchedCondition("source:web")).toBe("Source: web");
  });

  it("leaves a label with no separator unchanged", () => {
    expect(describeMatchedCondition("unrecognised")).toBe("unrecognised");
  });
});

describe("year/size range helpers (#775)", () => {
  it("parses a full range", () => {
    expect(parseRange("2015-2020")).toEqual({ min: "2015", max: "2020" });
  });

  it("parses an open-ended range with no minimum", () => {
    expect(parseRange("-2020")).toEqual({ min: "", max: "2020" });
  });

  it("parses an open-ended range with no maximum", () => {
    expect(parseRange("2015-")).toEqual({ min: "2015", max: "" });
  });

  it("round-trips through formatRange", () => {
    expect(formatRange(parseRange("2015-2020").min, parseRange("2015-2020").max)).toBe("2015-2020");
  });
});

describe("slugForFileName (#777)", () => {
  it("turns a rule name into a safe file name", () => {
    expect(slugForFileName("WEB Tier 01 (Streaming)")).toBe("web-tier-01-streaming");
  });

  it("falls back to a generic name when nothing is left", () => {
    expect(slugForFileName("***")).toBe("custom-format");
  });
});
