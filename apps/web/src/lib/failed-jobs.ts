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
  const jobs = await fetchAllPages<JobQueueItem>("/api/jobs?failed=true&pageSize=80");
  // An API from before the filter ignores it and answers with every job.
  return jobs.filter((job) => isJobFailed(job.status as JobStatus));
}

/** A person has decided this failed job does not need doing. */
export async function dismissFailedJob(jobId: string): Promise<void> {
  const response = await authedFetch(`/api/jobs/${encodeURIComponent(jobId)}/dismiss`, { method: "POST" });
  // 404 means it is no longer a failed job: retried, revived or already dismissed.
  if (!response.ok && response.status !== 404) throw new Error("That job could not be dismissed.");
}
