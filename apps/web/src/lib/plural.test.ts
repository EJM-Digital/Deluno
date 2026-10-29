import { describe, expect, it } from "vitest";
import { plural, pluralNoun, pluralWord } from "./plural";

describe("plural (#1423)", () => {
  it("reads 1 movie library and 2 movie libraries", () => {
    expect(plural(1, "movie library")).toBe("1 movie library");
    expect(plural(2, "movie library")).toBe("2 movie libraries");
  });

  it("reads 0 as plural and puts a thousands separator in a big count", () => {
    expect(plural(0, "title")).toBe("0 titles");
    expect(plural(1200, "title")).toBe("1,200 titles");
  });

  it("pluralises the English way: ies after a consonant and y, es after s x ch sh, s otherwise", () => {
    expect(pluralWord("entry")).toBe("entries");
    expect(pluralWord("day")).toBe("days");
    expect(pluralWord("dispatch")).toBe("dispatches");
    expect(pluralWord("match")).toBe("matches");
    expect(pluralWord("box")).toBe("boxes");
    expect(pluralWord("class")).toBe("classes");
    expect(pluralWord("rush")).toBe("rushes");
    expect(pluralWord("import")).toBe("imports");
    expect(pluralWord("TV library")).toBe("TV libraries");
  });

  it("takes an explicit plural when the words change with the count", () => {
    expect(plural(1, "download has", "downloads have")).toBe("1 download has");
    expect(plural(3, "download has", "downloads have")).toBe("3 downloads have");
  });
});

describe("pluralNoun (#1423)", () => {
  it("gives only the word, for a figure drawn apart from it", () => {
    expect(pluralNoun(1, "peer")).toBe("peer");
    expect(pluralNoun(14, "peer")).toBe("peers");
    expect(pluralNoun(2, "library")).toBe("libraries");
  });
});
