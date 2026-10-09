import { describe, expect, it } from "vitest";
import { formatResetTime, localizeResetTimes } from "./reset-times";

describe("localizeResetTimes", () => {
  it("shows a UTC stamp as the viewer's own clock time, not the server's", () => {
    const now = new Date(Date.UTC(2026, 9, 9, 0, 10));
    const text = localizeResetTimes("GitHub's limit is used up until 2026-10-09 00:35 UTC. Deluno will try again then.", now);
    const expected = formatResetTime(new Date(Date.UTC(2026, 9, 9, 0, 35)), now);
    expect(text).toBe(`GitHub's limit is used up until ${expected}. Deluno will try again then.`);
    expect(text).not.toContain("UTC");
  });

  it("names the day when the time is not today", () => {
    const now = new Date(Date.UTC(2026, 9, 9, 0, 10));
    const later = new Date(Date.UTC(2026, 9, 12, 12, 0));
    expect(formatResetTime(later, now)).toBe(`${later.toLocaleDateString([], { weekday: "short" })} ${later.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}`);
  });

  it("leaves text without a stamp alone", () => {
    expect(localizeResetTimes("Couldn't check for updates right now.")).toBe("Couldn't check for updates right now.");
  });
});
