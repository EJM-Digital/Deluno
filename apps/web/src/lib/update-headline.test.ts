import { describe, expect, it } from "vitest";
import { updateHeadline } from "./update-headline";

const base = { restartRequired: false, updateAvailable: false, state: "up-to-date", message: "You are on the latest version." };

describe("updateHeadline", () => {
  it("says Up to date only when the check found that out", () => {
    expect(updateHeadline(base)).toBe("Up to date");
  });

  it("never says Up to date while the State is an error", () => {
    expect(updateHeadline({ ...base, state: "error", message: "Update check failed." })).toBe("Update needs attention");
  });

  it("names the GitHub limit and when to retry, as the server words it", () => {
    expect(updateHeadline({ ...base, state: "error", message: "Couldn't check for updates (GitHub limit, retry after 9:21 PM)." }))
      .toBe("Couldn't check for updates (GitHub limit, retry after 9:21 PM)");
  });

  it("keeps the restart and update-available headlines", () => {
    expect(updateHeadline({ ...base, restartRequired: true })).toBe("Restart to finish updating");
    expect(updateHeadline({ ...base, updateAvailable: true, state: "update-available" })).toBe("Update available");
  });
});
