import { describe, expect, it } from "vitest";
import {
  clampDownloadHealthMinutes,
  clampDownloadHealthStrikes,
  downloadHealthMinutesProblem,
  downloadHealthStrikesProblem
} from "./download-health-bounds";

describe("#1475 download health bounds", () => {
  it.each([[0, 1], [1, 1], [999, 999], [1000, 999]])("brings %i minutes and strikes to %i", (value, used) => {
    expect(clampDownloadHealthMinutes(value)).toBe(used);
    expect(clampDownloadHealthStrikes(value)).toBe(used);
  });

  it.each([[0, false], [1, true], [999, true], [1000, false]])("takes %i only when it is in range: %s", (value, ok) => {
    expect(downloadHealthMinutesProblem(value, "The time") === undefined).toBe(ok);
    expect(downloadHealthStrikesProblem(value, "Strikes") === undefined).toBe(ok);
  });

  it("says the range in plain words", () => {
    expect(downloadHealthMinutesProblem(1000, "The time")).toBe("The time must be between 1 and 999 minutes.");
    expect(downloadHealthStrikesProblem(0, "Strikes")).toBe("Strikes must be between 1 and 999.");
  });
});
