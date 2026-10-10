import { describe, expect, it } from "vitest";
import type { ProcessorOutcomeRecord } from "./api";
import { processorOutcomeSummary } from "./processor-outcome";

const owed: ProcessorOutcomeRecord = {
  handoffId: "h-1",
  processorName: "Weir",
  libraryId: "library-1",
  releaseName: "Tears.of.Steel.2012.1080p-DELUNO",
  outcome: "imported",
  importedPath: "X:/Films/Tears of Steel (2012).mkv",
  reason: null,
  occurredUtc: "2026-09-23T07:46:41Z",
  delivery: "pending",
  attempts: 3,
  lastAttemptUtc: "2026-09-23T07:50:41Z",
  nextAttemptUtc: "2026-09-23T07:54:41Z",
  released: false,
  processorSaid: "Deluno could not reach Weir.",
  recordedUtc: "2026-09-23T07:46:41Z"
};

const when = (utc: string) => `at ${utc.slice(11, 16)}`;

describe("processorOutcomeSummary (#580)", () => {
  it("says a report still owed is not heard yet, and when it is next tried", () => {
    const summary = processorOutcomeSummary(owed, when);

    expect(summary.heard).toBe("Not yet · 3 attempts · next at 07:54");
    expect(summary.detail).toBe("Deluno could not reach Weir.");
    expect(summary.canSendAgain).toBe(true);
  });

  it("offers nothing more once the processor has heard, and does not repeat a released copy", () => {
    const summary = processorOutcomeSummary({ ...owed, delivery: "delivered", released: true, processorSaid: "Released." }, when);

    expect(summary.heard).toBe("Yes, and it released its copy");
    expect(summary.detail).toBeNull();
    expect(summary.canSendAgain).toBe(false);
  });

  it("keeps the processor's reason for keeping its copy", () => {
    const summary = processorOutcomeSummary({ ...owed, delivery: "delivered", processorSaid: "Nothing for Weir to remove." }, when);

    expect(summary.heard).toBe("Yes, and it kept its copy");
    expect(summary.detail).toBe("Nothing for Weir to remove.");
  });

  it.each([
    ["refused", "Refused"],
    ["given-up", "Deluno stopped trying"]
  ])("lets a person say a %s report again", (delivery, heard) => {
    const summary = processorOutcomeSummary({ ...owed, delivery }, when);

    expect(summary.heard).toBe(heard);
    expect(summary.canSendAgain).toBe(true);
  });

  it.each([
    ["never-received", "It never received this hand-off, so there was nothing to tell it"],
    ["replaced", "A newer send of this release took over, so this one was not told"]
  ])("has nothing to say again for a %s report", (delivery, heard) => {
    const summary = processorOutcomeSummary({ ...owed, delivery }, when);

    expect(summary.heard).toBe(heard);
    expect(summary.canSendAgain).toBe(false);
  });

  it("says what Deluno told it in either direction", () => {
    expect(processorOutcomeSummary(owed, when).said).toBe("It has the file");
    expect(processorOutcomeSummary({ ...owed, outcome: "not-imported" }, when).said).toBe("It will not import the file");
  });
});
