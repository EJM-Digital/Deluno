import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { describe, expect, it } from "vitest";
import { ACTIVITY_KINDS } from "./activity-kinds";
import { activityKind, isRoutine } from "./kinds";

/** Every category the server labels (and so every category it can write). */
const LABELS = (() => {
  let folder = process.cwd();
  while (!existsSync(join(folder, "src", "Deluno.Contracts", "ActivityEventLabels.cs")) && dirname(folder) !== folder) folder = dirname(folder);
  return join(folder, "src", "Deluno.Contracts", "ActivityEventLabels.cs");
})();
const categories = [...readFileSync(LABELS, "utf8").matchAll(/^\s+\["([^"]+)"\]\s*=/gm)].map((match) => match[1]);

describe("the Dashboard's decision for every activity category (#1491)", () => {
  it("reads the server's labels", () => {
    expect(categories.length).toBeGreaterThan(200);
  });

  it("has a decision for every category the server can write, and none for one it cannot", () => {
    const decided = new Set(Object.keys(ACTIVITY_KINDS).map((category) => category.toLowerCase()));
    expect(categories.filter((category) => !decided.has(category.toLowerCase()))).toEqual([]);
    const known = new Set(categories.map((category) => category.toLowerCase()));
    expect(Object.keys(ACTIVITY_KINDS).filter((category) => !known.has(category.toLowerCase()))).toEqual([]);
  });

  it("reads every category as its table says, whatever the case", () => {
    for (const category of categories) {
      expect(activityKind(category), category).toBe(ACTIVITY_KINDS[category]);
      expect(activityKind(category.toUpperCase()), category).toBe(ACTIVITY_KINDS[category]);
    }
  });

  it("shows what happened to a title or download", () => {
    const shown: Record<string, string> = {
      "download.health.remediated": "failed",
      "release.blocked": "failed",
      "release.block.proposed": "failed",
      "release.block-lifted": "cleanup",
      "download.dispatch.recorded": "grab",
      "download.dispatch.failed": "failed",
      "download.follow-up.refused": "failed",
      "download.queue.removed": "cleanup",
      "download.vanished": "failed",
      "download.integrity.failed": "failed",
      "filesystem.import.completed": "import",
      "filesystem.import.failed": "failed",
      "filesystem.import.named": "import",
      "processing.completed": "process",
      "download.cleanup.cleared": "cleanup",
      "library.movie.renamed": "process",
      "library.search.cycle": "search",
      "movie.release.grabbed": "grab",
      "movie.removed": "cleanup"
    };
    for (const [category, kind] of Object.entries(shown)) {
      expect(activityKind(category), category).toBe(kind);
      expect(isRoutine(activityKind(category)), category).toBe(false);
    }
  });

  it("leaves out only machinery with no title", () => {
    for (const category of ["job.started", "job.completed", "job.queued", "metadata.movie.refreshed", "metadata.artwork.cleanup", "download.client.connectivity-retry-succeeded", "download.health.remediation-previewed", "release.turned-down", "tags.renamed", "quality.model.reset"]) {
      expect(isRoutine(activityKind(category)), category).toBe(true);
    }
  });

  it("shows a category the table does not know yet if it says it failed, and any row the server marks as an error", () => {
    expect(activityKind("download.something.failed")).toBe("failed");
    expect(activityKind("download.health.something-new", "error")).toBe("failed");
    expect(activityKind("job.started", "error")).toBe("failed");
  });

  it("reads a category the table does not know yet by its pattern, one pattern at a time", () => {
    const byPattern: Record<string, string> = {
      "download.brand-new.problem": "failed",
      "processing.stalled.brand-new": "failed",
      "job.brand-new": "other",
      "download.health.brand-new": "other",
      "retention.brand-new": "retention",
      "x.search.brand-new": "search",
      "rss.brand-new": "search",
      "x.release.force-grabbed": "grab",
      "x.dispatch": "grab",
      "x.import.completed": "import",
      "import.brand-new": "import",
      "x.orphan.brand-new": "cleanup",
      "x.recycle.brand-new": "cleanup",
      "processor.brand-new": "process",
      "subtitle.brand-new": "subtitle",
      "request.brand-new": "lists",
      "zzz.brand-new": "other"
    };
    for (const [category, kind] of Object.entries(byPattern)) {
      expect(Object.keys(ACTIVITY_KINDS).includes(category), category).toBe(false);
      expect(activityKind(category), category).toBe(kind);
    }
  });

  it("reads a row with no category as routine work, never as a title's event", () => {
    expect(activityKind(null as unknown as string)).toBe("other");
    expect(activityKind(undefined as unknown as string)).toBe("other");
  });
});
