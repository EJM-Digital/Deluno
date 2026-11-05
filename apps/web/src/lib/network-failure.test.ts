import { describe, expect, it } from "vitest";
import { COULD_NOT_REACH_DELUNO, isNetworkFailure, plainFailureMessage } from "./network-failure";

/** #1474: a request nothing answered is said in plain words on every screen, not as the browser's own text. */
describe("plainFailureMessage", () => {
  it.each([
    ["Chrome", "Failed to fetch"],
    ["Safari", "Load failed"],
    ["Firefox", "NetworkError when attempting to fetch resource."]
  ])("says Deluno could not be reached for %s's own words (%s)", (_browser, own) => {
    expect(isNetworkFailure(new TypeError(own))).toBe(true);
    expect(plainFailureMessage(new TypeError(own), "That could not be saved.")).toBe(COULD_NOT_REACH_DELUNO);
  });

  it("keeps the words of any other error, because they were written for a person", () => {
    expect(plainFailureMessage(new Error("That name is taken."), "fallback")).toBe("That name is taken.");
  });

  it("uses the handler's own sentence when the error has no words or is not an error", () => {
    expect(plainFailureMessage(new Error(""), "fallback")).toBe("fallback");
    expect(plainFailureMessage("boom", "fallback")).toBe("fallback");
    expect(plainFailureMessage(undefined, "fallback")).toBe("fallback");
    expect(isNetworkFailure(new Error("Failed to fetch"))).toBe(false);
  });
});
