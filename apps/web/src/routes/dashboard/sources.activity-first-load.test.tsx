import { render, renderHook, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

/**
 * #1491: the panel opens on the newest page of the history. Two hundred routine rows written after five strikes
 * must not fill that page. The fake server below answers as the real one does: newest first, the categories it
 * is told to leave out left out, the page size applied last.
 */
const history = vi.hoisted(() => {
  const rows = [
    ...Array.from({ length: 200 }, (_, i) => ({ id: `job${i}`, category: i % 2 ? "job.completed" : "job.started", message: `Task ${i}`, createdUtc: "2026-11-05T04:31:00Z", relatedEntityType: null, relatedEntityId: null })),
    ...Array.from({ length: 5 }, (_, i) => ({ id: `strike${i}`, category: "download.health.remediated", message: `Strike ${i}`, createdUtc: "2026-11-05T04:29:00Z", relatedEntityType: "movie", relatedEntityId: `m${i}` }))
  ];
  return rows;
});
vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchPageItems: async (path: string) => {
    const url = new URL(path, "http://x");
    const left = new Set((url.searchParams.get("excludeCategories") ?? "").split(",").filter(Boolean));
    return history.filter((row) => !left.has(row.category)).slice(0, Number(url.searchParams.get("pageSize")));
  }
}));

const { liveSources } = await import("./sources");
const { useLiveActivity } = await import("./live-events");
const { ActivityStream } = await import("./live/stream");
const { NO_TITLES } = await import("./live/titles");

describe("the Activity panel's first load (#1491)", () => {
  it("holds the five strikes however many routine rows were written after them", async () => {
    const loaded = await liveSources.latestActivity.fetch();
    expect(loaded.filter((row) => row.category === "download.health.remediated")).toHaveLength(5);
    const { result } = renderHook(() => useLiveActivity(loaded));
    render(<MemoryRouter><ActivityStream rows={result.current} titles={NO_TITLES} /></MemoryRouter>);
    const text = screen.getByTestId("live-stream").textContent ?? "";
    for (let i = 0; i < 5; i++) expect(text).toContain(`Strike ${i}`);
  });
});
