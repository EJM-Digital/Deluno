/**
 * Background jobs are the jobs that are not about a title (#1114): a library-wide metadata refresh,
 * a backup, a scheduled pass. They are listed, retried and dismissed on System. A job about a title
 * (a search, an import, a subtitle fetch) names it in `relatedEntityId` and is shown in Activity.
 */
import type { JobQueueItem } from "./api";
import { isJobRetrying } from "./job-status-constants";

export const SYSTEM_BACKGROUND_JOBS_LINK = "/system";

/** Importing a download is always about a file and a title, and In progress owns it, whether or not the job names the title. */
const TITLE_JOB_TYPES = new Set(["filesystem.import.execute"]);

export function isBackgroundJob(job: Pick<JobQueueItem, "relatedEntityId"> & { jobType?: string }): boolean {
  if (job.jobType && TITLE_JOB_TYPES.has(job.jobType)) return false;
  return !job.relatedEntityId || job.relatedEntityId.trim() === "";
}

/** Failed jobs a person must deal with (not ones Deluno will try again by itself), split by where they are cleared. */
export function splitStoppedJobs<T extends JobQueueItem>(failed: T[]): { background: T[]; title: T[] } {
  const stopped = failed.filter((job) => !isJobRetrying(job));
  return { background: stopped.filter(isBackgroundJob), title: stopped.filter((job) => !isBackgroundJob(job)) };
}
