import { describe, expect, it } from "vitest";
import type { RetentionField } from "./retention-step-builder";
import { operatorLabel, takesValue } from "./retention-step-builder";

/**
 * The words a retention condition's operator reads as, by the kind of field it is asked of, and which operators ask for a value
 * after them. The drawer's operator list and the rules list's summary both use these words.
 */

const field = (overrides: Partial<RetentionField>): RetentionField => ({
  id: "x", label: "X", hint: "", group: "server", valueKind: "number", operators: [], options: null, source: "server", unsupportedOn: [], ...overrides
} as RetentionField);

describe("operator words", () => {
  it("a field about people reads nobody, anyone and everyone, and the list words for the rest", () => {
    const people = field({ valueKind: "users" });
    expect(operatorLabel(people, "nobody")).toBe("nobody");
    expect(operatorLabel(people, "anyone")).toBe("anyone");
    expect(operatorLabel(people, "everyone")).toBe("everyone");
    expect(operatorLabel(people, "in")).toBe("includes any of");
    expect(operatorLabel(people, "all")).toBe("includes all of");
    expect(operatorLabel(people, "notin")).toBe("includes none of");
    expect(operatorLabel(people, "set")).toBe("is not empty");
    expect(operatorLabel(people, "unset")).toBe("is empty");
    expect(operatorLabel(field({ valueKind: "textList" }), "in")).toBe("includes any of");
  });

  it("a date from a media server reads more than … days ago, within the last … days and within the next … days", () => {
    const date = field({ valueKind: "date", group: "server" });
    expect(operatorLabel(date, "beyond")).toBe("more than … days ago");
    expect(operatorLabel(date, "within")).toBe("within the last … days");
    expect(operatorLabel(date, "next")).toBe("within the next … days");
  });

  it("a viewing date reads at some point or never, not is empty", () => {
    const viewed = field({ valueKind: "date", group: "watching" });
    expect(operatorLabel(viewed, "set")).toBe("at some point");
    expect(operatorLabel(viewed, "unset")).toBe("never");
    expect(operatorLabel(field({ valueKind: "date", group: "server" }), "unset")).not.toBe("never");
  });

  it("a number or text field keeps the library filter's own words, and an unknown operator reads as itself", () => {
    expect(operatorLabel(field({ valueKind: "number" }), "min")).toBe("at least");
    expect(operatorLabel(field({ valueKind: "number" }), "max")).toBe("at most");
    expect(operatorLabel(field({ valueKind: "text" }), "has")).toBe("contains");
    expect(operatorLabel(undefined, "somethingNew")).toBe("somethingNew");
  });
});

describe("which operators ask for a value", () => {
  it("set, unset, nobody, anyone and everyone stand alone; every other operator takes a value", () => {
    for (const operator of ["set", "unset", "nobody", "anyone", "everyone"]) expect(takesValue(operator), operator).toBe(false);
    for (const operator of ["beyond", "within", "next", "in", "all", "notin", "min", "max", "is", "has"]) expect(takesValue(operator), operator).toBe(true);
  });
});
