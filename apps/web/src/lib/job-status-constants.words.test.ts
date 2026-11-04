import { describe, expect, it } from "vitest";
import {
  getDispatchStatusLabel,
  getJobStatusLabel,
  getJobStatusTone,
  getStatusLabel,
  isJobActive,
  isJobDeadLettered,
  isJobDone,
  isJobFailed,
  isJobInProgress,
  isJobPending,
  isJobRetrying,
  isJobSuccessful,
  JOB_STATUS,
  type JobStatus
} from "./job-status-constants";

const ALL: JobStatus[] = ["queued", "running", "completed", "failed", "dead-letter"];

describe("what each job status means", () => {
  it.each([
    ["queued", { active: true, progress: false, pending: true, done: false, ok: false, failed: false, dead: false }],
    ["running", { active: true, progress: true, pending: false, done: false, ok: false, failed: false, dead: false }],
    ["completed", { active: false, progress: false, pending: false, done: true, ok: true, failed: false, dead: false }],
    ["failed", { active: false, progress: false, pending: false, done: true, ok: false, failed: true, dead: false }],
    ["dead-letter", { active: false, progress: false, pending: false, done: true, ok: false, failed: true, dead: true }]
  ] as const)("%s", (status, expected) => {
    expect({
      active: isJobActive(status),
      progress: isJobInProgress(status),
      pending: isJobPending(status),
      done: isJobDone(status),
      ok: isJobSuccessful(status),
      failed: isJobFailed(status),
      dead: isJobDeadLettered(status)
    }).toEqual(expected);
  });

  it("uses the words the backend writes", () => {
    expect(JOB_STATUS).toEqual({ QUEUED: "queued", RUNNING: "running", COMPLETED: "completed", FAILED: "failed", DEAD_LETTER: "dead-letter" });
  });
});

describe("a failed job that will be tried again", () => {
  const job = { status: "failed", attempts: 1, maxAttempts: 3, nextAttemptUtc: "2026-11-04T10:00:00Z" };

  it("is retrying, not failed", () => {
    expect(isJobRetrying(job)).toBe(true);
  });

  it("is not retrying once it has used its last attempt, or when no next attempt is planned, or when it did not fail", () => {
    expect(isJobRetrying({ ...job, attempts: 3 })).toBe(false);
    expect(isJobRetrying({ ...job, attempts: 2 })).toBe(true);
    expect(isJobRetrying({ ...job, nextAttemptUtc: null })).toBe(false);
    expect(isJobRetrying({ ...job, status: "running" })).toBe(false);
    expect(isJobRetrying({ ...job, status: "dead-letter" })).toBe(false);
  });
});

describe("the words and colours of a job status", () => {
  it.each([
    ["queued", "Queued", "info"],
    ["running", "Running", "info"],
    ["completed", "Completed", "ok"],
    ["failed", "Failed", "bad"],
    ["dead-letter", "Gave up", "warn"]
  ] as const)("%s reads %s in %s", (status, label, tone) => {
    expect(getJobStatusLabel(status)).toBe(label);
    expect(getJobStatusTone(status)).toBe(tone);
    expect(getStatusLabel(status)).toBe(label);
  });

  it("gives every status a label and a tone", () => {
    for (const status of ALL) {
      expect(getJobStatusLabel(status)).not.toBe(status);
      expect(getJobStatusTone(status)).not.toBe("idle");
    }
  });

  it("shows a status it does not know as it is, in a quiet colour", () => {
    expect(getJobStatusLabel("mystery" as JobStatus)).toBe("mystery");
    expect(getJobStatusTone("mystery" as JobStatus)).toBe("idle");
  });
});

describe("the words for a download that was sent", () => {
  it.each([
    ["sent", "Sent"],
    ["failed", "Failed"],
    ["planned", "Needs URL"],
    ["imported", "Imported"],
    ["importFailed", "Import failed"]
  ])("%s reads %s", (status, label) => {
    expect(getDispatchStatusLabel(status)).toBe(label);
    expect(getStatusLabel(status === "failed" ? "planned" : status)).toBe(status === "failed" ? "Needs URL" : label);
  });

  it("turns a word nobody wrote a label for into a sentence", () => {
    expect(getDispatchStatusLabel("series.search")).toBe("Series search");
    expect(getDispatchStatusLabel("some_new-state")).toBe("Some new state");
    expect(getDispatchStatusLabel("waitingForClient")).toBe("Waiting for client");
    expect(getDispatchStatusLabel("v2Ready")).toBe("V2 ready");
    expect(getStatusLabel("importPending")).toBe("Import pending");
  });
});
