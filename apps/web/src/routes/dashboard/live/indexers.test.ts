import { describe, expect, it } from "vitest";
import { pulseOf, SLOW_ANSWER_MS } from "./indexers";

const answered = { phase: "answered" as const, resultCount: 12, elapsedMs: 300, failed: false, failure: null };

describe("what an indexer is doing, from the events it pushes", () => {
  it("says it is being asked while the question is out", () => {
    expect(pulseOf({ ...answered, phase: "asked" })).toEqual({ phase: "asking", tone: "info", words: "being asked" });
  });

  it("shows a failure in red with its reason", () => {
    expect(pulseOf({ ...answered, failed: true, failure: "timed out" })).toEqual({ phase: "answered", tone: "bad", words: "failed: timed out" });
  });

  it("shows a failure with no reason as plain failed", () => {
    expect(pulseOf({ ...answered, failed: true })).toEqual({ phase: "answered", tone: "bad", words: "failed" });
  });

  it("shows a quick answer in green with the count and the time", () => {
    expect(pulseOf(answered)).toEqual({ phase: "answered", tone: "ok", words: "12 results in 300 ms" });
    expect(pulseOf({ ...answered, resultCount: 1 }).words).toBe("1 result in 300 ms");
  });

  it("counts an answer of exactly the slow limit as quick, and one a millisecond over as slow", () => {
    expect(pulseOf({ ...answered, elapsedMs: SLOW_ANSWER_MS }).tone).toBe("ok");
    expect(pulseOf({ ...answered, elapsedMs: SLOW_ANSWER_MS + 1 }).tone).toBe("warn");
  });

  it("says answered when it did not report a count, and 0 ms when it did not report a time", () => {
    expect(pulseOf({ ...answered, resultCount: null, elapsedMs: null })).toEqual({ phase: "answered", tone: "ok", words: "answered in 0 ms" });
  });
});
