import { describe, expect, it } from "vitest";
import { activityEventType, categoryLabel } from "./activity-format";

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
