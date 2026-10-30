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

/**
 * A job that failed an attempt and has another one coming is retrying, not
 * failed: Deluno tries it again on its own, and nobody needs to do anything.
 *
 * <p>The backend writes "failed" for both, with the next attempt's time beside
 * it. Every import of a processed file failed once, because its first attempt
 * found the processor still finishing with the file, so In progress, Activity and
 * the sidebar each counted a failure for half a minute on an import that then
 * succeeded (#562).</p>
 */
export const isJobRetrying = (job: {
  status: string;
  attempts: number;
  maxAttempts: number;
  nextAttemptUtc: string | null;
}): boolean =>
  job.status === JOB_STATUS.FAILED && job.nextAttemptUtc !== null && job.attempts < job.maxAttempts;

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
 * Words for a download dispatch's status: a grab handed to a client, and what
 * became of it. Four screens each kept their own copy of this switch (#644),
 * and two had fallen behind -- a title's page showed a failed import as the raw
 * "importFailed".
 */
const DISPATCH_STATUS_LABELS: Record<string, string> = {
  sent: "Sent",
  failed: "Failed",
  planned: "Needs URL",
  imported: "Imported",
  importFailed: "Import failed",
};

/** "importFailed" / "dead-letter" / "series.search" -> sentence case, for a word no table knows yet. */
const sentenceCase = (value: string): string => {
  const spaced = value.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[._-]/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
};

export const getDispatchStatusLabel = (status: string): string =>
  DISPATCH_STATUS_LABELS[status] ?? sentenceCase(status);

/**
 * Any status word the backend writes where the screen cannot know which kind
 * it is -- a decision explanation's status is a job's, a dispatch's or a
 * search's. The job table answers first, so a job that ran out of attempts
 * reads "Gave up" here too, not "Dead letter".
 */
export const getStatusLabel = (status: string): string => {
  const jobLabel = getJobStatusLabel(status as JobStatus);
  if (jobLabel !== status) return jobLabel;
  return getDispatchStatusLabel(status);
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
