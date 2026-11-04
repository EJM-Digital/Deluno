import { describe, expect, it } from "vitest";
import { plainReason } from "./plain";

describe("a stuck download's reason in plain words", () => {
  it.each([
    [401, "The processor refused Deluno's key, so it was not cleaned."],
    [403, "The processor refused Deluno's key, so it was not cleaned."],
    [404, "The processor could not find it, so it was not cleaned."],
    [500, "The processor hit an error, so it was not cleaned."],
    [503, "The processor hit an error, so it was not cleaned."],
    [400, "The processor refused the hand-off."],
    [499, "The processor refused the hand-off."]
  ])("a hand-off refused with HTTP %i reads as: %s", (status, words) => {
    const said = `The processor refused the hand-off with HTTP ${status}: nope`;
    expect(plainReason(said)).toEqual({ text: words, full: said });
  });

  it.each([
    ["The request timed out", "The processor took too long and Deluno stopped waiting."],
    ["It took too long", "The processor took too long and Deluno stopped waiting."],
    ["Host unreachable", "Deluno could not reach the processor."],
    ["It could not be reached", "Deluno could not reach the processor."],
    ["connection refused", "Deluno could not reach the processor."],
    ["There is no connection", "Deluno could not reach the processor."],
    ["We could not connect", "Deluno could not reach the processor."],
    ["The output folder was not found", "The processor finished but its cleaned files are not there."],
    ["Cleaned files never appeared", "The processor finished but its cleaned files are not there."],
    ["result does not exist", "The processor finished but its cleaned files are not there."],
    ["Missing or invalid torrent data", "Torrent data missing or broken."],
    ["The client no longer lists this download", "The download client no longer has it."],
    ["The client no longer has the download", "The download client no longer has it."],
    ["It stopped moving", "It stopped moving in the download client."],
    ["No one is sharing it", "Nobody is sharing it."],
    ["There are no peers", "Nobody is sharing it."],
    ["no seeds", "Nobody is sharing it."],
    ["Nobody is sharing", "Nobody is sharing it."]
  ])("%s reads as: %s", (said, words) => {
    expect(plainReason(said)?.text).toBe(words);
  });

  it("keeps what was originally said as the tooltip, trimmed", () => {
    expect(plainReason("  stopped moving  ")).toEqual({ text: "It stopped moving in the download client.", full: "stopped moving" });
  });

  it("leaves a sentence it does not recognise exactly as it was said", () => {
    expect(plainReason("Something odd happened")).toEqual({ text: "Something odd happened", full: "Something odd happened" });
  });

  it("says nothing when there is nothing to say", () => {
    expect(plainReason(null)).toBeNull();
    expect(plainReason(undefined)).toBeNull();
    expect(plainReason("   ")).toBeNull();
  });

  it('understands a hand-off written without the hyphen, in any case', () => {
    expect(plainReason('REFUSED THE HANDOFF WITH HTTP 403')?.text).toBe("The processor refused Deluno's key, so it was not cleaned.");
  });

  it('understands every way the cleaned output can be missing, and ignores a sentence that merely mentions an output', () => {
    expect(plainReason('The cleaned folder was not found')?.text).toBe('The processor finished but its cleaned files are not there.');
    expect(plainReason('The cleaned files are missing')?.text).toBe('The processor finished but its cleaned files are not there.');
    expect(plainReason('The result was not there')?.text).toBe('The processor finished but its cleaned files are not there.');
    expect(plainReason('The output looks fine')?.text).toBe('The output looks fine');
  });
});
