import { describe, expect, it } from "vitest";
import {
  JOB_STATUS,
  getJobStatusLabel,
  isJobActive,
  isJobDeadLettered,
  isJobDone,
  isJobFailed,
  isJobSuccessful
} from "./job-status-constants";

describe("job status", () => {
  // The backend writes "dead-letter" for a job that exhausted its retries and
  // counts it as failed everywhere. The UI did not know the word, so a
  // dead-lettered import was invisible to every failure count and could not be
  // retried from Activity (#249).
  it("treats a dead letter as failed", () => {
    expect(isJobFailed(JOB_STATUS.DEAD_LETTER)).toBe(true);
    expect(isJobFailed(JOB_STATUS.FAILED)).toBe(true);
    expect(isJobDone(JOB_STATUS.DEAD_LETTER)).toBe(true);
    expect(isJobSuccessful(JOB_STATUS.DEAD_LETTER)).toBe(false);
    expect(isJobActive(JOB_STATUS.DEAD_LETTER)).toBe(false);
  });

  it("distinguishes a dead letter from a retryable failure", () => {
    expect(isJobDeadLettered(JOB_STATUS.DEAD_LETTER)).toBe(true);
    expect(isJobDeadLettered(JOB_STATUS.FAILED)).toBe(false);
  });

  it("labels a dead letter in plain language", () => {
    expect(getJobStatusLabel(JOB_STATUS.DEAD_LETTER)).toBe("Gave up");
  });

  it("keeps success and activity unchanged", () => {
    expect(isJobSuccessful(JOB_STATUS.COMPLETED)).toBe(true);
    expect(isJobActive(JOB_STATUS.QUEUED)).toBe(true);
    expect(isJobActive(JOB_STATUS.RUNNING)).toBe(true);
    expect(isJobFailed(JOB_STATUS.COMPLETED)).toBe(false);
  });
});
