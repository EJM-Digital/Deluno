import { describe, expect, it } from "vitest";
import { describeCondition, encodeCondition, parseCondition, type RetentionField } from "./retention-step-builder";

const PERSON_FIELD: RetentionField = {
  id: "server.personViews",
  label: "Times one person finished it",
  hint: "",
  group: "watching",
  valueKind: "integer",
  operators: ["min", "max", "is"],
  options: null,
  source: "server",
  unsupportedOn: [],
  perPerson: true
};

describe("per-person rule conditions", () => {
  it("carries whose answer it wants in the field, safe for colons and at signs", () => {
    const encoded = encodeCondition("server.personViews", "min", "3", "Ben: the @ one");

    expect(encoded).toBe("server.personViews@Ben%3A%20the%20%40%20one:min:3");
    expect(parseCondition(encoded)).toEqual({ field: "server.personViews", person: "Ben: the @ one", operator: "min", value: "3" });
  });

  it("leaves an ordinary field without a person", () => {
    expect(encodeCondition("server.timesViewed", "min", "3")).toBe("server.timesViewed:min:3");
    expect(parseCondition("server.timesViewed:min:3").person).toBe("");
  });

  it("says whose in the sentence", () => {
    expect(describeCondition("server.personViews@owner:min:3", [PERSON_FIELD])).toContain("(owner)");
  });
});
