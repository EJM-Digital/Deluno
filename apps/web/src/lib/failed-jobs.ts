/**
 * The jobs that need a person, wherever they sit in the queue.
 *
 * Every screen used to look for failures in "the latest N jobs". Recurring work
 * (the media read alone adds two rows every half hour) pushes a failure out of
 * any such window within hours, so the sidebar — which read every page — went
 * on saying "2 failed jobs" while Activity, the screen it sends you to, said
 * "none failing" and kept its Retry button disabled. The failure could then be
 * neither seen nor cleared from any screen.
 *
 * One reader, asked for failures by name, is what the sidebar, Activity and
 * Transfers now share, so they cannot disagree about the number.
 */
import { fetchAllPages, type JobQueueItem } from "./api";
import { isJobFailed, type JobStatus } from "./job-status-constants";
import { authedFetch } from "./use-auth";

export async function fetchFailedJobs(): Promise<JobQueueItem[]> {
  const jobs = await fetchAllPages<JobQueueItem>("/api/jobs?failed=true&pageSize=500&includePayload=true");
  // An API from before the filter ignores it and answers with every job.
  return jobs.filter((job) => isJobFailed(job.status as JobStatus));
}

/** A person has decided this failed job does not need doing. */
export async function dismissFailedJob(jobId: string): Promise<void> {
  const response = await authedFetch(`/api/jobs/${encodeURIComponent(jobId)}/dismiss`, { method: "POST" });
  // 404 means it is no longer a failed job: retried, revived or already dismissed.
  if (!response.ok && response.status !== 404) throw new Error("That job could not be dismissed.");
}

/** Tries one failed job again, and no other: Try again on a single failure (#1155). */
export async function retryFailedJob(jobId: string): Promise<void> {
  const response = await authedFetch(`/api/jobs/${encodeURIComponent(jobId)}/retry`, { method: "POST" });
  if (!response.ok) throw new Error("That job could not be tried again.");
}

/**
 * A person has seen this failed send and does not need it listed as failed (#1218). The send leaves Activity's Failed
 * count; the block list is untouched ("Lift the block" is its own action).
 */
export async function dismissFailedDispatch(dispatchId: string): Promise<void> {
  const response = await authedFetch(`/api/download-dispatches/${encodeURIComponent(dispatchId)}/dismiss`, { method: "POST" });
  // 404 or 409: it is gone, or no longer a failed send, so there is nothing left to clear.
  if (!response.ok && response.status !== 404 && response.status !== 409) throw new Error("That download could not be dismissed.");
}
