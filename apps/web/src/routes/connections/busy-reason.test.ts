import { describe, expect, it } from "vitest";
import { removeUnavailableReason } from "./busy-reason";

describe("why Remove is unavailable", () => {
  it("says nothing when nothing else is running", () => {
    expect(removeUnavailableReason(null)).toBeNull();
  });

  it("names what it is waiting for", () => {
    expect(removeUnavailableReason("test:form")).toBe("Waiting for the connection test to finish");
    expect(removeUnavailableReason("save")).toBe("Waiting for the save to finish");
    expect(removeUnavailableReason("remove")).toBe("Removing…");
    expect(removeUnavailableReason("reset:abc")).toBe("Waiting for the reset to finish");
    expect(removeUnavailableReason("reset:")).toBe("Waiting for the reset to finish");
  });

  it("says a current change is running for anything else, including a name that only looks like one it knows", () => {
    expect(removeUnavailableReason("test:other")).toBe("Waiting for the current change to finish");
    expect(removeUnavailableReason("saving")).toBe("Waiting for the current change to finish");
    expect(removeUnavailableReason("xreset:1")).toBe("Waiting for the current change to finish");
    expect(removeUnavailableReason("")).toBe("Waiting for the current change to finish");
  });
});
