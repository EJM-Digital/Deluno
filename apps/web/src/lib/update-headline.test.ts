import { formatResetTime } from "./reset-times";
import { describe, expect, it } from "vitest";
import { latestVersionLabel, updateHeadline } from "./update-headline";

const base = { restartRequired: false, updateAvailable: false, state: "up-to-date", message: "You are on the latest version." };

describe("updateHeadline", () => {
  it("says Up to date only when the check found that out", () => {
    expect(updateHeadline(base)).toBe("Up to date");
  });

  it("never says Up to date while the State is an error", () => {
    expect(updateHeadline({ ...base, state: "error", message: "Update check failed." })).toBe("Update needs attention");
  });

  it("names the GitHub limit and shows when to retry in the viewer's own time, not the server's", () => {
    const text = updateHeadline({ ...base, state: "error", message: "Couldn't check for updates (GitHub limit, retry after 2026-10-09 00:35 UTC)." });
    const local = formatResetTime(new Date(Date.UTC(2026, 9, 9, 0, 35)));
    expect(text).toBe(`Couldn't check for updates (GitHub limit, retry after ${local})`);
    expect(text).not.toContain("UTC");
  });

  it("keeps the restart and update-available headlines", () => {
    expect(updateHeadline({ ...base, restartRequired: true })).toBe("Restart to finish updating");
    expect(updateHeadline({ ...base, updateAvailable: true, state: "update-available" })).toBe("Update available");
  });
});

describe("latestVersionLabel", () => {
  it("names the version when one is known", () => {
    expect(latestVersionLabel({ state: "update-available", latestVersion: "1.2.3" })).toBe("1.2.3");
  });

  it("says there is no update only when the check ran", () => {
    expect(latestVersionLabel({ state: "up-to-date", latestVersion: null })).toBe("No update available");
  });

  it("says the check could not run after a failed check", () => {
    expect(latestVersionLabel({ state: "error", latestVersion: null })).toBe("Couldn't check");
  });
});
