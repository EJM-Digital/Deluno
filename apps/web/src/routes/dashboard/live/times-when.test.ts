import { describe, expect, it } from "vitest";
import { clockTime, whenText } from "./times";

describe("when a run is due, beyond tomorrow", () => {
  it("names the weekday and the clock time, in the same words as a time today or tomorrow", () => {
    const now = new Date("2026-10-01T10:00:00").getTime();
    const later = new Date("2026-10-04T15:30:00").getTime();
    expect(whenText(later, now)).toBe(`${new Date(later).toLocaleDateString("en-AU", { weekday: "short" })} ${clockTime(later)}`);
    expect(whenText(later, now)).toMatch(/^Sun 3:30 pm$/);
    expect(whenText(now - 1, now)).toBe("now");
  });
});
