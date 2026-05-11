// Shared job status constants - use these instead of string literals throughout the app
// This ensures consistency and reduces duplication

export const JOB_STATUS = {
  QUEUED: "queued",
  RUNNING: "running",
  COMPLETED: "completed",
  FAILED: "failed",
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
  status === JOB_STATUS.COMPLETED || status === JOB_STATUS.FAILED;

export const isJobSuccessful = (status: JobStatus): boolean =>
  status === JOB_STATUS.COMPLETED;

export const isJobFailed = (status: JobStatus): boolean =>
  status === JOB_STATUS.FAILED;

// UI-friendly status labels
export const getJobStatusLabel = (status: JobStatus): string => {
  const labels: Record<JobStatus, string> = {
    [JOB_STATUS.QUEUED]: "Queued",
    [JOB_STATUS.RUNNING]: "Running",
    [JOB_STATUS.COMPLETED]: "Completed",
    [JOB_STATUS.FAILED]: "Failed",
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
      return "destructive";
    default:
      return "default";
  }
};
