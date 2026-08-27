// Shared job status constants - use these instead of string literals throughout the app
// This ensures consistency and reduces duplication
import { statusTone, type Tone } from "./status-tones";

export const JOB_STATUS = {
  QUEUED: "queued",
  RUNNING: "running",
  COMPLETED: "completed",
  FAILED: "failed",
  // A job that exhausted its retries. The backend has always written this
  // status and counts it as failed; the UI did not know the word, so a
  // dead-lettered job was invisible to every failure count and could not be
  // retried from Activity (#249).
  DEAD_LETTER: "dead-letter",
} as const;

export type JobStatus = (typeof JOB_STATUS)[keyof typeof JOB_STATUS];

// Helper functions for common status checks
export const isJobActive = (status: JobStatus): boolean =>
  status === JOB_STATUS.QUEUED || status === JOB_STATUS.RUNNING;

export const isJobInProgress = (status: JobStatus): boolean =>
  status === JOB_STATUS.RUNNING;

export const isJobPending = (status: JobStatus): boolean =>
  status === JOB_STATUS.QUEUED;

export const isJobDone = (status: JobStatus): boolean =>
  status === JOB_STATUS.COMPLETED || isJobFailed(status);

export const isJobSuccessful = (status: JobStatus): boolean =>
  status === JOB_STATUS.COMPLETED;

export const isJobFailed = (status: JobStatus): boolean =>
  status === JOB_STATUS.FAILED || status === JOB_STATUS.DEAD_LETTER;

/** A dead letter is failed *and* out of retries, so it needs a person. */
export const isJobDeadLettered = (status: JobStatus): boolean =>
  status === JOB_STATUS.DEAD_LETTER;

// UI-friendly status labels
export const getJobStatusLabel = (status: JobStatus): string => {
  const labels: Record<JobStatus, string> = {
    [JOB_STATUS.QUEUED]: "Queued",
    [JOB_STATUS.RUNNING]: "Running",
    [JOB_STATUS.COMPLETED]: "Completed",
    [JOB_STATUS.FAILED]: "Failed",
    [JOB_STATUS.DEAD_LETTER]: "Gave up",
  };
  return labels[status] ?? status;
};

/**
 * The tone a job status wears, from the one table.
 *
 * Running was amber and Queued was grey. #290 names both blue by name: a job in
 * flight is motion and a queued one is motion that has not started, and neither
 * needs a person — which is exactly what amber had been claiming of every job
 * Deluno ran. A dead letter keeps amber, because it is out of retries and does
 * need one.
 */
export const getJobStatusTone = (status: JobStatus): Tone => {
  switch (status) {
    case JOB_STATUS.QUEUED:
      return statusTone("job.queued");
    case JOB_STATUS.RUNNING:
      return statusTone("job.running");
    case JOB_STATUS.COMPLETED:
      return statusTone("job.completed");
    case JOB_STATUS.DEAD_LETTER:
      return statusTone("job.deadLetter");
    case JOB_STATUS.FAILED:
      return statusTone("job.failed");
    default:
      return "idle";
  }
};
