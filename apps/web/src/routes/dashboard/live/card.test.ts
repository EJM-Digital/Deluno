import { describe, expect, it } from "vitest";
import { initialsOf } from "./card";

describe("the poster tile", () => {
  it("reads two letters from the title, leaving out articles, years and episode codes", () => {
    expect(initialsOf("The Wild Robot (2024)")).toBe("WR");
    expect(initialsOf("Lioness S03E08")).toBe("L");
    expect(initialsOf("Say Yes to the Dress S04E07")).toBe("SY");
    expect(initialsOf("An import did not finish")).toBe("ID");
  });
});
