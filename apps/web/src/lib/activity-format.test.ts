import { describe, expect, it } from "vitest";
import { categoryLabel } from "./activity-format";

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
