import { describe, expect, it } from "vitest";
import type { JobQueueItem } from "./api";
import { isBackgroundJob, splitStoppedJobs } from "./background-jobs";

const job = (over: Record<string, unknown>) => ({ id: "j", jobType: "library.scan", status: "dead-letter", attempts: 3, maxAttempts: 3, nextAttemptUtc: null, relatedEntityId: null, ...over }) as unknown as JobQueueItem;

describe("background jobs (#1114)", () => {
  it("are the jobs that name no title", () => {
    expect(isBackgroundJob(job({ relatedEntityId: null }))).toBe(true);
    expect(isBackgroundJob(job({ relatedEntityId: "" }))).toBe(true);
    expect(isBackgroundJob(job({ relatedEntityId: "  " }))).toBe(true);
    expect(isBackgroundJob(job({ relatedEntityId: "m1", relatedEntityType: "movie" }))).toBe(false);
  });

  it("never include an import, which In progress and Activity own", () => {
    expect(isBackgroundJob(job({ jobType: "filesystem.import.execute", relatedEntityId: null }))).toBe(false);
  });

  it("split stopped jobs by where they are cleared, leaving out one Deluno will retry by itself", () => {
    const split = splitStoppedJobs([
      job({ id: "bg" }),
      job({ id: "ti", relatedEntityId: "m1" }),
      job({ id: "retrying", status: "failed", attempts: 1, nextAttemptUtc: "2026-10-02T00:00:00Z" })
    ]);
    expect(split.background.map((j) => j.id)).toEqual(["bg"]);
    expect(split.title.map((j) => j.id)).toEqual(["ti"]);
  });
});
