import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const handlers = vi.hoisted(() => ({ byEvent: new Map<string, () => void>() }));
vi.mock("./use-signalr", () => ({
  RealtimeGroups: { Title: (id: string) => `title:${id}` },
  useSignalREvent: (event: string, _subject: string, handler: () => void) => { handlers.byEvent.set(event, handler); }
}));

vi.mock("./live-ticker", () => ({
  scheduleOnce: (ms: number, run: () => void) => { const id = setTimeout(run, ms); return () => clearTimeout(id); },
  subscribeTick: () => () => undefined
}));

const { useTitleLive } = await import("./use-title-live");

interface Page { cast: string[]; files: string[]; subtitles: string[] }
const base: Page = { cast: ["a"], files: ["f1"], subtitles: [] };
const fire = (event: string) => act(() => handlers.byEvent.get(event)!());
const settle = () => act(async () => { await vi.advanceTimersByTimeAsync(500); });

beforeEach(() => { vi.useFakeTimers(); handlers.byEvent.clear(); });
afterEach(() => vi.useRealTimers());

describe("a title page that keeps itself current", () => {
  it("shows the page's own data until the title changes", () => {
    const { result } = renderHook(() => useTitleLive("t1", base, {}, {}));
    expect(result.current).toEqual(base);
  });

  it("re-reads only the groups an event moves, once per burst, and shows the new answer", async () => {
    const files = vi.fn().mockResolvedValue({ files: ["f1", "f2"] });
    const subtitles = vi.fn().mockResolvedValue({ subtitles: ["en"] });
    const { result } = renderHook(() => useTitleLive<Page>("t1", base, { files, subtitles, cast: vi.fn() }, { ImportStateChanged: ["files"], MovieChanged: ["files", "subtitles"] }));

    fire("ImportStateChanged"); fire("MovieChanged"); fire("ImportStateChanged");
    await settle();

    expect(files).toHaveBeenCalledTimes(1);
    expect(subtitles).toHaveBeenCalledTimes(1);
    expect(result.current).toEqual({ cast: ["a"], files: ["f1", "f2"], subtitles: ["en"] });
  });

  it("keeps the old answer for a group that fails to read, and ignores a group nobody defined", async () => {
    const files = vi.fn().mockRejectedValue(new Error("offline"));
    const { result } = renderHook(() => useTitleLive<Page>("t1", base, { files }, { SeriesChanged: ["files", "ghost"] }));
    fire("SeriesChanged");
    await settle();
    expect(files).toHaveBeenCalled();
    expect(result.current).toEqual(base);
  });

  it("ignores an event that moves nothing, and every other event name is wired", async () => {
    const files = vi.fn().mockResolvedValue({ files: ["x"] });
    const { result } = renderHook(() => useTitleLive<Page>("t1", base, { files }, { JourneyStageChanged: [], RecoveryCaseChanged: ["files"] }));
    fire("JourneyStageChanged");
    fire("ReleasesTurnedDown");
    await settle();
    expect(files).not.toHaveBeenCalled();
    fire("RecoveryCaseChanged");
    await settle();
    expect(result.current.files).toEqual(["x"]);
  });

  it("drops a pending read when the page's own data reloads first, and a late answer from before the reload", async () => {
    let release!: (value: Partial<Page>) => void;
    const files = vi.fn().mockImplementation(() => new Promise((resolve) => { release = resolve; }));
    const { result, rerender } = renderHook(({ data }) => useTitleLive<Page>("t1", data, { files }, { MovieChanged: ["files"] }), { initialProps: { data: base } });

    fire("MovieChanged");
    rerender({ data: { ...base, cast: ["b"] } });
    await settle();
    expect(files).not.toHaveBeenCalled();

    fire("MovieChanged");
    await settle();
    expect(files).toHaveBeenCalledTimes(1);
    rerender({ data: { ...base, cast: ["c"] } });
    await act(async () => { release({ files: ["stale"] }); });
    expect(result.current).toEqual({ cast: ["c"], files: ["f1"], subtitles: [] });
  });
});
