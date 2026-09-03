import { describe, expect, it } from "vitest";
import { verdictTone, verdictWords } from "./quality-build-steps";

/**
 * The verdict the live judgement shows.
 *
 * <p>This exists because the first version got it wrong in the way that is
 * hardest to notice and worst to ship: it compared against `"MeetsPlan"` while
 * the API serialises `"meetsPlan"`, so every verdict fell through to the
 * default and the panel read <i>"Deluno would refuse this"</i> above the reason
 * <i>"All upgrade-driving targets are met."</i> Nothing threw and nothing
 * logged — it just confidently said the opposite of the truth on the one screen
 * whose whole job is explaining itself.</p>
 */
describe("what the live judgement says", () => {
  it("answers each status in words rather than in Deluno's own vocabulary", () => {
    expect(verdictWords("meetsPlan")).toBe("Deluno would take this and stop looking");
    expect(verdictWords("belowGoal")).toBe("Deluno would take this and keep looking for better");
    expect(verdictWords("needsReview")).toBe("Deluno cannot tell from the name alone");
    expect(verdictWords("missing")).toBe("Deluno would refuse this");
  });

  it("does not care how the serialiser cases the status", () => {
    for (const status of ["meetsPlan", "MeetsPlan", "MEETSPLAN", "meetsplan"]) {
      expect(verdictWords(status), status).toBe("Deluno would take this and stop looking");
      expect(verdictTone(status), status).toBe("ok");
    }
  });

  it("says it could not tell, rather than inventing a refusal", () => {
    // A status nothing recognises is not a refusal. Reporting one would be the
    // exact defect this file was written for, arrived at from the other side.
    expect(verdictWords("somethingNew")).toBe("Deluno could not say");
    expect(verdictWords(undefined)).toBe("Deluno could not say");
    expect(verdictTone("somethingNew")).toBe("idle");
  });

  it("colours accepted, keep-looking and refused apart", () => {
    expect(verdictTone("meetsPlan")).toBe("ok");
    expect(verdictTone("belowGoal")).toBe("warn");
    expect(verdictTone("missing")).toBe("bad");
  });
});
