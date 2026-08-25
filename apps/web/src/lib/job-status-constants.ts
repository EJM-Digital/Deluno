// Shared job status constants - use these instead of string literals throughout the app
// This ensures consistency and reduces duplication

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

// Status color variants for UI (Tailwind/CSS)
export const getJobStatusVariant = (
  status: JobStatus
): "default" | "secondary" | "destructive" | "outline" | "success" | "warning" => {
  switch (status) {
    case JOB_STATUS.QUEUED:
      return "secondary";
    case JOB_STATUS.RUNNING:
      return "warning";
    case JOB_STATUS.COMPLETED:
      return "success";
    case JOB_STATUS.FAILED:
    case JOB_STATUS.DEAD_LETTER:
      return "destructive";
    default:
      return "default";
  }
};
