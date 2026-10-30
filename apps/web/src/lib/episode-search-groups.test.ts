import { describe, expect, it } from "vitest";
import type { JobQueueItem } from "./api";
import { describeEpisodeSearchGroup, groupEpisodeSearches } from "./episode-search-groups";

function job(id: string, status: string, episode: number, showId = "show-1"): JobQueueItem {
  return {
    id, jobType: "episode.search", label: "Search for an episode", source: "series", sourceLabel: "The TV library", status,
    payloadJson: null, attempts: 1, createdUtc: "2026-10-08T02:00:00Z", scheduledUtc: "2026-10-08T02:00:00Z", startedUtc: null,
    completedUtc: status === "completed" ? "2026-10-08T02:01:00Z" : null, leasedUntilUtc: null, workerId: null, lastError: null,
    relatedEntityType: "episode", relatedEntityId: `ep-${id}`, idempotencyKey: null, dedupeKey: null, maxAttempts: 3,
    lastAttemptUtc: null, nextAttemptUtc: null, target: `Severance S02E${String(episode).padStart(2, "0")}`, showId, showTitle: "Severance"
  };
}

describe("groupEpisodeSearches", () => {
  it("reads two or more active searches of a show as one entry and counts its parts", () => {
    const active = [job("a", "running", 1), job("b", "queued", 2), job("c", "queued", 10)];
    const finished = [job("d", "completed", 3)];
    const { groups, rest } = groupEpisodeSearches(active, finished);
    expect(rest).toHaveLength(0);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({ showTitle: "Severance", total: 4, done: 1, running: 1, waiting: 2 });
    expect(groups[0].jobs.map((item) => item.target)).toEqual(["Severance S02E01", "Severance S02E02", "Severance S02E03", "Severance S02E10"]);
    expect(describeEpisodeSearchGroup(groups[0])).toBe("4 episodes · 1 done · 1 running · 2 waiting");
  });

  it("leaves a single episode search ungrouped and other shows apart", () => {
    const { groups, rest } = groupEpisodeSearches([job("a", "queued", 1), job("b", "queued", 1, "show-2")], []);
    expect(groups).toHaveLength(0);
    expect(rest).toHaveLength(2);
  });
});

describe("groupEpisodeSearches, other shapes", () => {
  it("ignores a search that names no show, and other kinds of job", () => {
    const orphan = { ...job("a", "queued", 1), showId: null, showTitle: null } as unknown as JobQueueItem;
    const other = { ...job("b", "queued", 1), jobType: "movie.search" } as JobQueueItem;
    const { groups, rest } = groupEpisodeSearches([orphan, other, job("c", "queued", 2)], []);
    expect(groups).toHaveLength(0);
    expect(rest).toHaveLength(3);
  });

  it("orders searches with no episode label without failing", () => {
    const unnamed = (id: string) => ({ ...job(id, "queued", 1), target: null }) as unknown as JobQueueItem;
    const { groups } = groupEpisodeSearches([unnamed("a"), unnamed("b"), job("c", "queued", 3)], []);
    expect(groups[0].jobs).toHaveLength(3);
  });

  it("says only the parts that are not zero", () => {
    const { groups } = groupEpisodeSearches([job("a", "queued", 1), job("b", "queued", 2)], []);
    expect(describeEpisodeSearchGroup(groups[0])).toBe("2 episodes · 2 waiting");
    const running = groupEpisodeSearches([job("a", "running", 1), job("b", "running", 2)], []).groups[0];
    expect(describeEpisodeSearchGroup(running)).toBe("2 episodes · 2 running");
  });
});
