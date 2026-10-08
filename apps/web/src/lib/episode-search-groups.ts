/**
 * A show-wide search queues one episode search per episode. Activity reads them as one entry per show:
 * the show, how many episodes, and how many are done, running or waiting (#1180). The episodes stay
 * reachable inside it. A single episode search is not grouped; it is just named.
 */
import type { JobQueueItem } from "./api";
import { isJobInProgress, isJobSuccessful, type JobStatus } from "./job-status-constants";

export const EPISODE_SEARCH = "episode.search";

export interface EpisodeSearchGroup {
  showId: string;
  showTitle: string;
  /** Every episode search of the show in this run, active ones and finished ones, ordered by episode. */
  jobs: JobQueueItem[];
  total: number;
  done: number;
  running: number;
  waiting: number;
  /** The newest activity among the jobs, for ordering and the "when" column. */
  whenUtc: string;
}

/** Finished searches this close to the first queued one still belong to the same run. */
const SAME_RUN_SLACK_MS = 10 * 60 * 1000;

const jobWhen = (job: JobQueueItem) => job.completedUtc ?? job.startedUtc ?? job.scheduledUtc;

/**
 * Splits the active jobs into show groups (two or more active episode searches of one show) and the rest.
 * `finished` supplies the completed searches so a group can say how many are done.
 */
export function groupEpisodeSearches(
  active: JobQueueItem[],
  finished: JobQueueItem[]
): { groups: EpisodeSearchGroup[]; rest: JobQueueItem[] } {
  const byShow = new Map<string, JobQueueItem[]>();
  for (const job of active) {
    if (job.jobType !== EPISODE_SEARCH || !job.showId || !job.showTitle) continue;
    byShow.set(job.showId, [...(byShow.get(job.showId) ?? []), job]);
  }

  const groups: EpisodeSearchGroup[] = [];
  const grouped = new Set<string>();
  for (const [showId, members] of byShow) {
    if (members.length < 2) continue;
    const since = Math.min(...members.map((job) => new Date(job.createdUtc).getTime())) - SAME_RUN_SLACK_MS;
    const completed = finished.filter(
      (job) => job.jobType === EPISODE_SEARCH && job.showId === showId && isJobSuccessful(job.status as JobStatus) && new Date(job.createdUtc).getTime() >= since
    );
    const jobs = [...members, ...completed].sort((a, b) => (a.target ?? "").localeCompare(b.target ?? "", undefined, { numeric: true }));
    for (const job of members) grouped.add(job.id);
    groups.push({
      showId,
      showTitle: members[0].showTitle!,
      jobs,
      total: jobs.length,
      done: completed.length,
      running: members.filter((job) => isJobInProgress(job.status as JobStatus)).length,
      waiting: members.filter((job) => !isJobInProgress(job.status as JobStatus)).length,
      whenUtc: jobs.map(jobWhen).sort().at(-1)!
    });
  }

  return { groups, rest: active.filter((job) => !grouped.has(job.id)) };
}

/** "12 episodes · 3 done · 2 running · 7 waiting", leaving out the parts that are zero. */
export function describeEpisodeSearchGroup(group: EpisodeSearchGroup): string {
  return [
    `${group.total} episodes`,
    group.done ? `${group.done} done` : null,
    group.running ? `${group.running} running` : null,
    group.waiting ? `${group.waiting} waiting` : null
  ].filter(Boolean).join(" · ");
}
