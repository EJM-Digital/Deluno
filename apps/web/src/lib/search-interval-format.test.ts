import { describe, expect, it } from "vitest";
import { formatSearchInterval } from "./search-interval-format";

describe("formatSearchInterval", () => {
  it("says minutes for anything under an hour", () => {
    expect(formatSearchInterval(15)).toBe("Every 15 minutes");
    expect(formatSearchInterval(30)).toBe("Every 30 minutes");
    expect(formatSearchInterval(1)).toBe("Every 1 minute");
  });

  it("says hours once the value divides evenly into hours", () => {
    expect(formatSearchInterval(60)).toBe("Every hour");
    expect(formatSearchInterval(180)).toBe("Every 3 hours");
    expect(formatSearchInterval(360)).toBe("Every 6 hours");
    expect(formatSearchInterval(720)).toBe("Every 12 hours");
  });

  it("says Daily for exactly a day", () => {
    expect(formatSearchInterval(1440)).toBe("Daily");
  });

  it("falls back to minutes for a value that does not divide evenly into hours", () => {
    expect(formatSearchInterval(90)).toBe("Every 90 minutes");
  });

  it("says Paused for a non-positive or invalid value", () => {
    expect(formatSearchInterval(0)).toBe("Paused");
    expect(formatSearchInterval(-5)).toBe("Paused");
    expect(formatSearchInterval(Number.NaN)).toBe("Paused");
  });
});
