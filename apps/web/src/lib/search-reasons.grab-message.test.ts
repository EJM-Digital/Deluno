import { describe, expect, it } from "vitest";
import { formatGrabMessage } from "./search-reasons";

describe("the message a grab leaves", () => {
  it("a release with no downloadable address yet is matched, not sent", () => {
    expect(formatGrabMessage("X", { dispatchStatus: "planned" })).toBe("Matched X, but no downloadable URL was available yet.");
  });

  it("a release the client rejected says so, with the client's reason when it gave one", () => {
    expect(formatGrabMessage("X", { dispatchStatus: "failed", dispatchMessage: "Out of space." })).toBe("Matched X, but the download client rejected it: Out of space.");
    expect(formatGrabMessage("X", { dispatchStatus: "failed" })).toBe("Matched X, but the download client rejected it.");
  });

  it("a forced grab is worded 'Forced' and an unknown status only says matched", () => {
    expect(formatGrabMessage("X", { forceOverride: true, dispatchStatus: "sent" })).toBe("Forced X to the download client.");
    expect(formatGrabMessage("X", { forceOverride: true })).toBe("Matched X.");
  });
});
