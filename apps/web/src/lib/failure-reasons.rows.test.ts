import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { failureKindWords, KNOWN_IMPORT_FAILURE_REASONS } from "./failure-reasons";

// #1329: the words for a failed import's kind, and the web's list of reasons held to the backend's.

describe("failureKindWords", () => {
  it.each([
    ["quality", "Quality rejected"],
    ["unmatched", "Needs matching"],
    ["corrupt", "Corrupt"],
    ["downloadFailed", "Download failed"],
    ["importFailed", "Import failed"],
    ["vanished-downloading", "Download vanished from the client"],
    ["processedFileIsLess", "Processor returned a smaller file"],
    ["processor-output-missing", "Missing from processor output"],
    ["processor-unmatched-output", "Unmatched in processor output"],
    ["processor-lost", "Processor lost the file"],
    ["processor-timeout", "Processor timed out"],
    ["processor-stalled", "Processor stalled"],
    ["processor-failed", "Processor failed"],
    ["processor-held", "Held by the processor"],
    ["processor-refuses-every-copy", "Processor refuses every copy"]
  ])("names the failure kind %s as '%s'", (kind, words) => {
    expect(failureKindWords(kind)).toBe(words);
  });

  it("never prints a kind it has no words for as a code: it splits it into a sentence", () => {
    expect(failureKindWords("somethingBrandNew")).toBe("Something brand new");
    expect(failureKindWords("some-other_kind")).toBe("Some other kind");
    expect(failureKindWords("")).toBe("Needs review");
  });

  it("has words for every reason the backend can store", () => {
    for (const reason of KNOWN_IMPORT_FAILURE_REASONS) expect(failureKindWords(reason)).not.toBe(reason);
  });
});

describe("the web's list of import-failure reasons against the backend's", () => {
  const source = readFileSync(resolve(__dirname, "../../../../src/Deluno.Contracts/ImportFailurePolicy.cs"), "utf8");

  /** `public const string Name = "value";` for every constant in ImportFailurePolicy. */
  const constants = new Map<string, string>([...source.matchAll(/public const string (\w+) = "([^"]+)";/g)].map((match) => [match[1], match[2]]));

  /** The names listed inside `KnownReasons = [ ... ];`. */
  const known = (() => {
    const list = /KnownReasons\s*=\s*\[([\s\S]*?)\];/.exec(source);
    if (!list) throw new Error("KnownReasons was not found in ImportFailurePolicy.cs");
    return list[1].split(",").map((name) => name.trim()).filter(Boolean);
  })();

  it("lists the same reasons, in the same order, as ImportFailurePolicy.KnownReasons", () => {
    const backend = known.map((name) => {
      const value = constants.get(name);
      if (value === undefined) throw new Error(`KnownReasons names ${name}, which has no constant`);
      return value;
    });

    expect(backend.length).toBeGreaterThan(10);
    expect([...KNOWN_IMPORT_FAILURE_REASONS]).toEqual(backend);
  });
});
