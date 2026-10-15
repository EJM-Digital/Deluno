import { act, renderHook } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { toast } from "../components/shell/toaster";
import { authedFetch } from "../lib/use-auth";
import type { MediaItem } from "../lib/media-types";
import { useBulkEdit } from "./use-bulk-edit";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../lib/api")>(),
  fetchJson: vi.fn().mockResolvedValue([])
}));

/**
 * Undo and Redo of the last bulk change (coverage register, area library): each undo sends the exact reverse call, each redo the
 * original one, and the toast says which. A library reassignment that moved files is undone by reassigning back with the same
 * "move the files" choice, so the folders return to where they were.
 */
const item = (id: string, over: Partial<MediaItem> = {}): MediaItem => ({
  id, title: `Title ${id}`, year: 2020, type: "movie", poster: null, backdrop: null, quality: null, monitored: true, sizeGb: null, rating: null,
  genres: [], added: "1 Jan", overview: "", libraryId: "lib-a", minimumAvailability: "released", ...over
});

function useHarness(initial: MediaItem[]) {
  const [items, setItems] = useState<MediaItem[]>(initial);
  const bulk = useBulkEdit({ libraryItems: items, setLibraryItems: setItems, variant: "movies" });
  return { items, bulk };
}

const answer = (body: unknown) => ({ ok: true, json: async () => body }) as Response;
const sent = () => vi.mocked(authedFetch).mock.calls.map(([url, init]) => ({ url: String(url), body: JSON.parse(String((init as RequestInit).body)) }));

async function runOperation(result: { current: ReturnType<typeof useHarness> }, ids: string[], operation: Parameters<ReturnType<typeof useHarness>["bulk"]["openBulkTools"]>[0], prepare: () => void) {
  act(() => { result.current.bulk.setSelectedIds(ids); });
  act(() => { result.current.bulk.openBulkTools(operation); });
  act(prepare);
  await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });
  await act(async () => { await result.current.bulk.executeBulkToolsOperation(); });
}

beforeEach(() => {
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockResolvedValue(answer({ updated: 1, reassigned: 1, filesMoved: 1, moveFailures: [] }));
  vi.mocked(toast.success).mockClear();
  vi.mocked(toast.error).mockClear();
});

describe("Undo and Redo of a bulk monitoring change", () => {
  it("Undo sends each title back to the state it had and says the change was reverted, Redo applies it again", async () => {
    const { result } = renderHook(() => useHarness([item("a", { monitored: true }), item("b", { monitored: false })]));
    await runOperation(result, ["a", "b"], "monitoring", () => result.current.bulk.setBulkMonitored(false));
    expect(result.current.items.map((entry) => entry.monitored)).toEqual([false, false]);
    vi.mocked(authedFetch).mockClear();

    await act(async () => { await result.current.bulk.runUndo(); });

    expect(sent()).toEqual([
      { url: "/api/movies/monitoring", body: { movieIds: ["a"], monitored: true } },
      { url: "/api/movies/monitoring", body: { movieIds: ["b"], monitored: false } }
    ]);
    expect(result.current.items.map((entry) => entry.monitored)).toEqual([true, false]);
    expect(toast.success).toHaveBeenCalledWith("Monitoring change reverted");
    vi.mocked(authedFetch).mockClear();

    await act(async () => { await result.current.bulk.runRedo(); });

    expect(sent()).toEqual([{ url: "/api/movies/monitoring", body: { movieIds: ["a", "b"], monitored: false } }]);
    expect(toast.success).toHaveBeenCalledWith("Monitoring change re-applied");
  });

  it("with nothing to undo or redo it does nothing", async () => {
    const { result } = renderHook(() => useHarness([item("a")]));

    await act(async () => { await result.current.bulk.runUndo(); });
    await act(async () => { await result.current.bulk.runRedo(); });

    expect(authedFetch).not.toHaveBeenCalled();
  });

  it("an undo the server refuses says Undo failed and keeps the change on the stack", async () => {
    const { result } = renderHook(() => useHarness([item("a")]));
    await runOperation(result, ["a"], "monitoring", () => result.current.bulk.setBulkMonitored(false));
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, json: async () => ({}) } as Response);

    await act(async () => { await result.current.bulk.runUndo(); });

    expect(toast.error).toHaveBeenCalledWith("Undo failed.");
    expect(result.current.bulk.undoStack).toHaveLength(1);
  });
});

describe("Undo and Redo of a library reassignment", () => {
  it("Undo reassigns the titles back to the library they came from with the same move-the-files choice, and Redo sends them on again", async () => {
    const { result } = renderHook(() => useHarness([item("a", { libraryId: "lib-a" }), item("b", { libraryId: "lib-c" })]));
    await runOperation(result, ["a", "b"], "reassignLibrary", () => {
      result.current.bulk.setBulkTargetLibraryId("lib-b");
      result.current.bulk.setBulkMoveFiles(true);
    });
    expect(sent()).toEqual([
      { url: "/api/movies/bulk/reassign-library", body: { movieIds: ["a"], fromLibraryId: "lib-a", toLibraryId: "lib-b", moveFiles: true } },
      { url: "/api/movies/bulk/reassign-library", body: { movieIds: ["b"], fromLibraryId: "lib-c", toLibraryId: "lib-b", moveFiles: true } }
    ]);
    vi.mocked(authedFetch).mockClear();

    await act(async () => { await result.current.bulk.runUndo(); });

    expect(sent()).toEqual([
      { url: "/api/movies/bulk/reassign-library", body: { movieIds: ["a"], fromLibraryId: "lib-b", toLibraryId: "lib-a", moveFiles: true } },
      { url: "/api/movies/bulk/reassign-library", body: { movieIds: ["b"], fromLibraryId: "lib-b", toLibraryId: "lib-c", moveFiles: true } }
    ]);
    expect(result.current.items.map((entry) => entry.libraryId)).toEqual(["lib-a", "lib-c"]);
    expect(toast.success).toHaveBeenCalledWith("Library reassignment reverted");
    vi.mocked(authedFetch).mockClear();

    await act(async () => { await result.current.bulk.runRedo(); });

    expect(sent().map((call) => call.body)).toEqual([
      { movieIds: ["a"], fromLibraryId: "lib-a", toLibraryId: "lib-b", moveFiles: true },
      { movieIds: ["b"], fromLibraryId: "lib-c", toLibraryId: "lib-b", moveFiles: true }
    ]);
    expect(result.current.items.map((entry) => entry.libraryId)).toEqual(["lib-b", "lib-b"]);
    expect(toast.success).toHaveBeenCalledWith("Library reassignment re-applied");
  });

  it("a record-only reassignment is undone record-only, so no file is moved back that was never moved", async () => {
    const { result } = renderHook(() => useHarness([item("a", { libraryId: "lib-a" })]));
    await runOperation(result, ["a"], "reassignLibrary", () => result.current.bulk.setBulkTargetLibraryId("lib-b"));
    vi.mocked(authedFetch).mockClear();

    await act(async () => { await result.current.bulk.runUndo(); });

    expect(sent()).toEqual([{ url: "/api/movies/bulk/reassign-library", body: { movieIds: ["a"], fromLibraryId: "lib-b", toLibraryId: "lib-a", moveFiles: false } }]);
  });
});
