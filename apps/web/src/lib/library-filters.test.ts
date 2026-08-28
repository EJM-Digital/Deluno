import { describe, expect, it } from "vitest";
import { defaultDisplayOptions, parseDisplayOptions } from "./library-filters";
import {
  applyConditions,
  conditionCount,
  decodeCondition,
  describeCondition,
  encodeCondition,
  parseConditions,
  type FilterCondition,
  type FilterFieldSpec,
  type PosterOptionSpec
} from "./library-controls";

/**
 * The narrowing a shelf can be asked for, on the browser's side of the wire.
 *
 * A file called `library-filters.test.ts` was deleted in #302 along with the
 * client-side filter engine it tested — an engine nothing imported, holding a
 * second definition of states the server already owned. This is the opposite
 * shape: nothing here decides which titles match. It only counts what is being
 * asked, writes it onto a request and reads it back off a saved view, and those
 * are the three things that can go wrong silently.
 */

const POSTER_OPTIONS: PosterOptionSpec[] = [
  { id: "showTitle", label: "Title", description: "", defaultOn: true, line: false },
  { id: "showRating", label: "Rating", description: "", defaultOn: true, line: false },
  { id: "showSize", label: "Size", description: "", defaultOn: false, line: true },
  { id: "showCodec", label: "Codec", description: "", defaultOn: false, line: true }
];

const SIZE_FIELD: FilterFieldSpec = {
  id: "size",
  label: "Size on disk",
  hint: "",
  group: "file",
  valueKind: "gigabytes",
  operators: ["min", "max"],
  options: null
};

describe("filter conditions", () => {
  it("asks for nothing by default", () => {
    const params = new URLSearchParams();
    applyConditions(params, []);
    // The rule that keeps this free for anybody not using it: an unfiltered
    // page must send the request it sent before the feature existed.
    expect([...params.keys()]).toEqual([]);
    expect(conditionCount([])).toBe(0);
  });

  it("sends one f per question, and both ends of a range are two questions", () => {
    const params = new URLSearchParams();
    applyConditions(params, [
      { field: "quality", operator: "in", values: ["Remux 2160p", "WEB 2160p"] },
      { field: "size", operator: "min", values: ["5"] },
      { field: "size", operator: "max", values: ["40"] }
    ]);

    expect(params.getAll("f")).toEqual([
      "quality:in:Remux 2160p|WEB 2160p",
      "size:min:5",
      "size:max:40"
    ]);
  });

  it("does not send a row that is still waiting for a value", () => {
    // Picking a field creates the row before you have typed into it. Sending it
    // is a 400 from a server that is right to refuse — it happened, live, on
    // "Has a file", and emptied the shelf with "Could not load the library".
    const params = new URLSearchParams();
    applyConditions(params, [
      { field: "size", operator: "min", values: [] },
      { field: "videoCodec", operator: "has", values: ["  "] },
      { field: "releaseGroup", operator: "set", values: [] }
    ]);

    // The one with no value *needed* has nothing missing, so it goes.
    expect(params.getAll("f")).toEqual(["releaseGroup:set"]);
    // And the badge counts what is narrowing, not how many rows are on screen.
    expect(conditionCount([
      { field: "size", operator: "min", values: [] },
      { field: "releaseGroup", operator: "set", values: [] }
    ])).toBe(1);
  });

  it("keeps a zero, because zero is an answer", () => {
    const params = new URLSearchParams();
    applyConditions(params, [{ field: "rating", operator: "min", values: ["0"] }]);
    expect(params.getAll("f")).toEqual(["rating:min:0"]);
  });

  it("survives a value carrying a colon, which a Windows path always does", () => {
    const condition: FilterCondition = { field: "path", operator: "starts", values: ["D:\\Media\\Films"] };
    // The server splits on the first two colons only. If the browser and the
    // server disagreed about that, a path filter would silently become a filter
    // for "D".
    expect(decodeCondition(encodeCondition(condition))).toEqual(condition);
  });

  it("reads a condition back as a sentence, with its unit", () => {
    expect(describeCondition({ field: "size", operator: "min", values: ["5"] }, SIZE_FIELD))
      .toBe("Size on disk at least 5 GB");
    expect(describeCondition({ field: "size", operator: "max", values: ["40"] }, undefined))
      .toBe("size at most 40");
  });
});

describe("reading a saved view", () => {
  it("migrates the nine-property filter set #324 replaced", () => {
    // Somebody's saved 4K view was written before conditions existed. Dropping
    // it would restore the shelf they were looking at without the filters that
    // produced it, and the difference is invisible until you count the titles.
    const stored = JSON.stringify({
      qualities: ["Remux 2160p"],
      genres: ["Drama", "Thriller"],
      minSizeGb: 5,
      maxSizeGb: null,
      minYear: 2000,
      maxYear: null,
      minRuntime: null,
      maxRuntime: null,
      minRating: 8
    });

    expect(parseConditions(stored)).toEqual([
      { field: "quality", operator: "in", values: ["Remux 2160p"] },
      // Two genres has always meant both, so the migration has to say `all`
      // rather than `in` or a saved view would quietly widen.
      { field: "genre", operator: "all", values: ["Drama", "Thriller"] },
      { field: "size", operator: "min", values: ["5"] },
      { field: "year", operator: "min", values: ["2000"] },
      { field: "rating", operator: "min", values: ["8"] }
    ]);
  });

  it("reads the legacy rule engine's rows as no filters at all", () => {
    // `rulesJson` held the browser rule list deleted in #302. It is not a filter
    // set, and reading it as one would narrow by something nobody asked for.
    expect(parseConditions(JSON.stringify([{ field: "status", op: "eq", value: "downloading" }]))).toEqual([]);
    expect(parseConditions("[]")).toEqual([]);
    expect(parseConditions("not json")).toEqual([]);
    expect(parseConditions(null)).toEqual([]);
  });

  it("round-trips the conditions it saved", () => {
    const conditions = [
      { field: "videoCodec", operator: "has" as const, values: ["x265"] },
      { field: "lastSearch", operator: "beyond" as const, values: ["90"] }
    ];
    expect(parseConditions(JSON.stringify(conditions))).toEqual(conditions);
  });
});

describe("display options", () => {
  it("gives a reader who has never chosen the essentials and none of the extras", () => {
    const options = defaultDisplayOptions(POSTER_OPTIONS);
    expect(options.showTitle).toBe(true);
    // The extras are the answer to "show me more". A card that arrives already
    // carrying everything has nothing left to ask for.
    expect(options.showSize).toBe(false);
    expect(options.showCodec).toBe(false);
  });

  it("fills in options a stored choice predates", () => {
    // Somebody who saved their display options before the extras existed must
    // not get `undefined` for them, which renders as neither on nor off — and
    // the option list now grows per media kind, so this matters more, not less.
    const options = parseDisplayOptions(JSON.stringify({ showTitle: false, showRating: false }), POSTER_OPTIONS);

    expect(options.showTitle).toBe(false);
    expect(options.showRating).toBe(false);
    expect(options.showSize).toBe(false);
    expect(options.showCodec).toBe(false);
  });

  it("ignores a stored switch the declaration no longer has", () => {
    // A poster option can be removed, or belong to the other media kind. It must
    // not come back as a switch nothing draws.
    const options = parseDisplayOptions(JSON.stringify({ showNextAiring: true }), POSTER_OPTIONS);
    expect(Object.keys(options).sort()).toEqual(["showCodec", "showRating", "showSize", "showTitle"]);
  });

  it("falls back rather than throwing on anything unreadable", () => {
    expect(parseDisplayOptions("not json", POSTER_OPTIONS)).toEqual(defaultDisplayOptions(POSTER_OPTIONS));
    expect(parseDisplayOptions(null, POSTER_OPTIONS)).toEqual(defaultDisplayOptions(POSTER_OPTIONS));
  });
});
