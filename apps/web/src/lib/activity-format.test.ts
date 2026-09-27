import { describe, expect, it } from "vitest";
import { activityEventType, categoryLabel, foldRoutineJobEvents, mattersToTheBell } from "./activity-format";

describe("activity category labels", () => {
  it("names the area in plain words, never the internal code", () => {
    expect(categoryLabel("job.failed")).toBe("Background task");
    expect(categoryLabel("download.dispatch.sent")).toBe("Downloads");
    expect(categoryLabel("search.completed")).toBe("Search");
    expect(categoryLabel("filesystem.import.failed")).toBe("Imports");
  });

  it("falls back to the first word for an area it does not know, without dots", () => {
    expect(categoryLabel("widget.thing.happened")).toBe("Widget");
    expect(categoryLabel("")).toBe("General");
  });
});

describe("activity event type (#798)", () => {
  it("buckets a refusal as blocklisted, not as a plain failure", () => {
    expect(activityEventType({ category: "release.blocked", severity: "info" })).toBe("blocklisted");
    expect(activityEventType({ category: "release.block.proposed", severity: "warning" })).toBe("blocklisted");
  });

  it("buckets a processing rejection as rejected", () => {
    expect(activityEventType({ category: "processing.rejected", severity: "warning" })).toBe("rejected");
  });

  it("buckets a completed import as imported", () => {
    expect(activityEventType({ category: "filesystem.import.completed", severity: "success" })).toBe("imported");
    expect(activityEventType({ category: "filesystem.import.season-pack.completed", severity: "success" })).toBe("imported");
  });

  it("buckets an import failure as failed, ahead of imported", () => {
    expect(activityEventType({ category: "filesystem.import.failed", severity: "error" })).toBe("failed");
  });

  it("buckets a queue removal as removed", () => {
    expect(activityEventType({ category: "download.queue.removed", severity: "info" })).toBe("removed");
  });

  it("buckets any error severity as failed even without a matching category keyword", () => {
    expect(activityEventType({ category: "download.client.failed", severity: "error" })).toBe("failed");
  });

  it("buckets a health event as health", () => {
    expect(activityEventType({ category: "health.problem", severity: "warning" })).toBe("health");
  });

  it("buckets a search event as search", () => {
    expect(activityEventType({ category: "library.search.executed", severity: "info" })).toBe("search");
    expect(activityEventType({ category: "episode.search.executed", severity: "info" })).toBe("search");
  });

  it("falls back to other for a category none of the buckets recognise", () => {
    expect(activityEventType({ category: "system", severity: "info" })).toBe("other");
  });
});

describe("what matters to the bell (#916)", () => {
  it("shows a failure, a grab, an import, a removal and a blocklist entry", () => {
    expect(mattersToTheBell({ category: "download.client.failed", severity: "error" })).toBe(true);
    expect(mattersToTheBell({ category: "download.dispatch.recorded", severity: "info" })).toBe(true);
    expect(mattersToTheBell({ category: "filesystem.import.completed", severity: "success" })).toBe(true);
    expect(mattersToTheBell({ category: "download.queue.removed", severity: "info" })).toBe(true);
    expect(mattersToTheBell({ category: "release.blocked", severity: "info" })).toBe(true);
    expect(mattersToTheBell({ category: "health.problem", severity: "warning" })).toBe(true);
  });

  it("hides a routine job lifecycle event and a search", () => {
    expect(mattersToTheBell({ category: "job.queued", severity: "info" })).toBe(false);
    expect(mattersToTheBell({ category: "job.started", severity: "info" })).toBe(false);
    expect(mattersToTheBell({ category: "job.completed", severity: "info" })).toBe(false);
    expect(mattersToTheBell({ category: "library.search.executed", severity: "info" })).toBe(false);
  });

  it("still shows a job that failed", () => {
    expect(mattersToTheBell({ category: "job.failed", severity: "error" })).toBe(true);
  });
});

describe("folding a job's lifecycle events into one row (#916)", () => {
  interface Event {
    id: string;
    category: string;
    relatedJobId: string | null;
  }

  it("folds queued, started and completed for the same job into one row", () => {
    const events: Event[] = [
      { id: "e3", category: "job.completed", relatedJobId: "job-1" },
      { id: "e2", category: "job.started", relatedJobId: "job-1" },
      { id: "e1", category: "job.queued", relatedJobId: "job-1" }
    ];

    const folded = foldRoutineJobEvents(events);

    expect(folded).toHaveLength(1);
    expect(folded[0]!.event.id).toBe("e3");
    expect(folded[0]!.foldedFrom.map((event) => event.id)).toEqual(["e2", "e1"]);
  });

  it("keeps two different job runs as two separate rows", () => {
    const events: Event[] = [
      { id: "movies-completed", category: "job.completed", relatedJobId: "job-movies" },
      { id: "tv-completed", category: "job.completed", relatedJobId: "job-tv" },
      { id: "movies-started", category: "job.started", relatedJobId: "job-movies" },
      { id: "tv-started", category: "job.started", relatedJobId: "job-tv" }
    ];

    const folded = foldRoutineJobEvents(events);

    expect(folded).toHaveLength(2);
    expect(folded[0]!.event.id).toBe("movies-completed");
    expect(folded[1]!.event.id).toBe("tv-completed");
  });

  it("leaves a non-lifecycle event alone", () => {
    const events: Event[] = [{ id: "e1", category: "release.blocked", relatedJobId: null }];

    const folded = foldRoutineJobEvents(events);

    expect(folded).toEqual([{ event: events[0], foldedFrom: [] }]);
  });
});
