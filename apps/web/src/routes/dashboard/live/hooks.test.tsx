import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useArrivals, useElementSize, useFreshKeys, useNow, useRemPx, useTitles } from "./hooks";
import type { JourneyCard, Stage } from "./journey";
import { NO_TITLES } from "./titles";

// #1329: the small hooks the Live board shares: the clock, the titles behind the cards, and what has just arrived.

const titles = vi.hoisted(() => ({ movies: vi.fn(), series: vi.fn(), byName: vi.fn() }));
vi.mock("./titles", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./titles")>()),
  fetchMovieTitles: titles.movies,
  fetchSeriesTitles: titles.series,
  fetchByName: titles.byName
}));

const card = (over: Partial<JourneyCard> & { key: string }): JourneyCard => ({ stage: "downloading", title: "x", state: { tone: "info", label: "Downloading" }, to: "/queue", ...over });

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>;
}

afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); vi.clearAllMocks(); });

describe("the clock, when it ticks slower than a second", () => {
  beforeEach(() => { vi.useFakeTimers(); });

  it("does not line itself up with the wall clock, and does not react to the tab coming back", () => {
    vi.setSystemTime(new Date(1_000_000_000_000));
    const { result } = renderHook(() => useNow(60_000));
    const start = result.current;
    vi.setSystemTime(new Date(1_000_000_000_000 + 30_000));
    act(() => { document.dispatchEvent(new Event("visibilitychange")); vi.advanceTimersByTime(1500); });
    expect(result.current).toBe(start);
  });
});

describe("the clock, when the tab is hidden", () => {
  beforeEach(() => { vi.useFakeTimers(); });

  it("ignores the tab coming back while it is still not visible", () => {
    vi.setSystemTime(new Date(1_000_000_000_000));
    const { result } = renderHook(() => useNow(1000));
    const start = result.current;
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    vi.setSystemTime(new Date(1_000_000_000_000 + 90_000));
    act(() => { document.dispatchEvent(new Event("visibilitychange")); });
    expect(result.current).toBe(start);
  });

  it("stops its listener when the board goes away", () => {
    const { unmount } = renderHook(() => useNow(1000));
    const remove = vi.spyOn(document, "removeEventListener");
    unmount();
    expect(remove).toHaveBeenCalledWith("visibilitychange", expect.any(Function));
  });
});

describe("the size of one rem", () => {
  it("is read from the page's font size", () => {
    document.documentElement.style.fontSize = "20px";
    expect(renderHook(() => useRemPx()).result.current).toBe(20);
    document.documentElement.style.fontSize = "";
  });

  it("is 16 when the page gives no size", () => {
    vi.spyOn(window, "getComputedStyle").mockReturnValue({ fontSize: "" } as CSSStyleDeclaration);
    expect(renderHook(() => useRemPx()).result.current).toBe(16);
  });
});

describe("cards that moved lane", () => {
  beforeEach(() => { vi.useFakeTimers(); });
  const at = (stage: Stage, key = "a") => [card({ key, stage })];

  it("marks nothing the first time the board is drawn", () => {
    const { result } = renderHook(() => useArrivals(at("downloading")));
    expect(result.current.size).toBe(0);
  });

  it("marks a card that moved lane, then lets it go after the glow", () => {
    const { result, rerender } = renderHook(({ cards }) => useArrivals(cards), { initialProps: { cards: at("downloading") } });
    rerender({ cards: at("importing") });
    expect([...result.current]).toEqual(["a"]);
    act(() => { vi.advanceTimersByTime(2399); });
    expect(result.current.size).toBe(1);
    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current.size).toBe(0);
  });

  it("marks nothing for a card that only arrived, or stayed where it was", () => {
    const { result, rerender } = renderHook(({ cards }) => useArrivals(cards), { initialProps: { cards: at("downloading") } });
    rerender({ cards: [...at("downloading"), card({ key: "b", stage: "importing" })] });
    expect(result.current.size).toBe(0);
  });

  it("restarts the glow when another card moves before the first has finished", () => {
    const { result, rerender } = renderHook(({ cards }) => useArrivals(cards), { initialProps: { cards: [...at("downloading", "a"), ...at("downloading", "b")] } });
    rerender({ cards: [...at("importing", "a"), ...at("downloading", "b")] });
    act(() => { vi.advanceTimersByTime(2000); });
    rerender({ cards: [...at("importing", "a"), ...at("importing", "b")] });
    expect([...result.current]).toEqual(["b"]);
    act(() => { vi.advanceTimersByTime(2000); });
    expect(result.current.size).toBe(1);
    act(() => { vi.advanceTimersByTime(400); });
    expect(result.current.size).toBe(0);
  });

  it("drops its timer when the board goes away mid-glow, and is fine to leave with none running", () => {
    const { rerender, unmount } = renderHook(({ cards }) => useArrivals(cards), { initialProps: { cards: at("downloading") } });
    rerender({ cards: at("importing") });
    const before = vi.getTimerCount();
    unmount();
    expect(vi.getTimerCount()).toBeLessThan(before);
    renderHook(() => useArrivals([])).unmount();
  });
});

describe("keys that appeared in a list", () => {
  beforeEach(() => { vi.useFakeTimers(); });

  it("marks nothing the first time the list is drawn", () => {
    expect(renderHook(() => useFreshKeys(["a", "b"])).result.current.size).toBe(0);
  });

  it("marks a key that appeared, for the hold time, and not a key that was already there", () => {
    const { result, rerender } = renderHook(({ keys }) => useFreshKeys(keys, 500), { initialProps: { keys: ["a"] } });
    rerender({ keys: ["a", "b"] });
    expect([...result.current]).toEqual(["b"]);
    act(() => { vi.advanceTimersByTime(499); });
    expect(result.current.size).toBe(1);
    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current.size).toBe(0);
  });

  it("uses the default hold of 1.6 seconds", () => {
    const { result, rerender } = renderHook(({ keys }) => useFreshKeys(keys), { initialProps: { keys: ["a"] } });
    rerender({ keys: ["a", "b"] });
    act(() => { vi.advanceTimersByTime(1599); });
    expect(result.current.size).toBe(1);
    act(() => { vi.advanceTimersByTime(1); });
    expect(result.current.size).toBe(0);
  });

  it("marks nothing when keys only leave, and restarts the hold when another key appears", () => {
    const { result, rerender, unmount } = renderHook(({ keys }) => useFreshKeys(keys, 500), { initialProps: { keys: ["a", "b"] } });
    rerender({ keys: ["a"] });
    expect(result.current.size).toBe(0);
    rerender({ keys: ["a", "c"] });
    act(() => { vi.advanceTimersByTime(400); });
    rerender({ keys: ["a", "c", "d"] });
    act(() => { vi.advanceTimersByTime(400); });
    expect([...result.current]).toEqual(["d"]);
    unmount();
  });

  it("is fine to leave with no timer running", () => {
    renderHook(() => useFreshKeys([])).unmount();
  });
});

describe("the library titles behind the cards", () => {
  beforeEach(() => {
    titles.movies.mockResolvedValue({ m1: { title: "Dune", year: 2021 } });
    titles.series.mockResolvedValue({ series: { s1: { title: "Shogun" } }, episodes: { e1: { seriesId: "s1", season: 1, episode: 2 } } });
    titles.byName.mockResolvedValue(null);
  });

  it("is the empty lookup when there is nothing to ask", () => {
    const { result } = renderHook(() => useTitles([card({ key: "a" })]), { wrapper });
    expect(result.current).toBe(NO_TITLES);
    expect(titles.movies).not.toHaveBeenCalled();
    expect(titles.series).not.toHaveBeenCalled();
    expect(titles.byName).not.toHaveBeenCalled();
  });

  it("asks once for the films and once for the shows and episodes, and says both are answered", async () => {
    const cards = [card({ key: "a", entity: { kind: "movie", id: "m1" } }), card({ key: "b", entity: { kind: "episode", id: "e1" } }), card({ key: "c", entity: { kind: "series", id: "s1" } })];
    const { result } = renderHook(() => useTitles(cards), { wrapper });
    await waitFor(() => expect(result.current.answered).toEqual({ movies: true, series: true }));
    expect(result.current.movies.get("m1")).toEqual({ title: "Dune", year: 2021 });
    expect(result.current.series.get("s1")).toEqual({ title: "Shogun" });
    expect(result.current.episodes.get("e1")).toMatchObject({ season: 1, episode: 2 });
    expect(titles.movies).toHaveBeenCalledWith(["m1"]);
    expect(titles.series).toHaveBeenCalledWith(["s1"], ["e1"]);
  });

  it("also asks for the titles named by the extra entities", async () => {
    const { result } = renderHook(() => useTitles([], [{ kind: "movie", id: "m1" }, undefined]), { wrapper });
    await waitFor(() => expect(result.current.movies.get("m1")?.title).toBe("Dune"));
    expect(titles.series).not.toHaveBeenCalled();
  });

  it("does not say the films are answered while they are still being asked for", async () => {
    let release!: (value: unknown) => void;
    titles.movies.mockReturnValue(new Promise((resolve) => { release = resolve; }));
    const cards = [card({ key: "a", entity: { kind: "movie", id: "m1" } }), card({ key: "b", entity: { kind: "series", id: "s1" } })];
    const { result } = renderHook(() => useTitles(cards), { wrapper });
    await waitFor(() => expect(result.current.series.get("s1")?.title).toBe("Shogun"));
    expect(result.current.answered).toEqual({ movies: false, series: true });
    await act(async () => { release({ m1: { title: "Dune" } }); });
    await waitFor(() => expect(result.current.answered?.movies).toBe(true));
  });

  it("copes with an answer that names no shows or episodes", async () => {
    titles.series.mockResolvedValue({});
    const { result } = renderHook(() => useTitles([card({ key: "a", entity: { kind: "series", id: "s1" } })], []), { wrapper });
    await waitFor(() => expect(result.current.answered?.series).toBe(true));
    expect(result.current.series.size).toBe(0);
    expect(result.current.episodes.size).toBe(0);
  });

  it("matches a download Deluno did not send to the library by its release name, a film with its year and a show without", async () => {
    titles.byName.mockImplementation(async (search: { kind: string; name: string }) => (search.name === "Dune" ? { id: "m9", info: { title: "Dune", year: 2021 } } : search.name === "Shogun" ? { id: "s9", info: { title: "Shogun" } } : null));
    const cards = [
      card({ key: "film", title: "Dune (2021)" }),
      card({ key: "show", title: "Shogun S01E02" }),
      card({ key: "unknown", title: "Nothing Here (2019)" }),
      card({ key: "plain", title: "no pattern" })
    ];
    const { result } = renderHook(() => useTitles(cards), { wrapper });
    await waitFor(() => expect(result.current.matched?.size).toBe(2));
    expect(result.current.matched?.get("film")).toEqual({ kind: "movie", id: "m9" });
    expect(result.current.matched?.get("show")).toEqual({ kind: "series", id: "s9" });
    expect(result.current.movies.get("m9")?.title).toBe("Dune");
    expect(result.current.series.get("s9")?.title).toBe("Shogun");
    expect(titles.byName).toHaveBeenCalledWith({ kind: "movie", name: "Dune", year: 2021 });
    expect(titles.byName).toHaveBeenCalledWith({ kind: "series", name: "Shogun", year: undefined });
  });

  it("looks a name up once however many cards carry it, and looks up at most 24 names", async () => {
    const same = [card({ key: "a", title: "Dune (2021)" }), card({ key: "b", title: "Dune (2021)" })];
    const first = renderHook(() => useTitles(same), { wrapper });
    await waitFor(() => expect(titles.byName).toHaveBeenCalledTimes(1));
    first.unmount();
    titles.byName.mockClear();
    const many = Array.from({ length: 30 }, (_, index) => card({ key: `k${index}`, title: `Film ${String(index).padStart(2, "0")} (2020)` }));
    renderHook(() => useTitles(many), { wrapper });
    await waitFor(() => expect(titles.byName).toHaveBeenCalledTimes(24));
  });
});

describe("an element's size, where it cannot be watched", () => {
  function Probe({ attach }: { attach: boolean }) {
    const [ref, size] = useElementSize<HTMLDivElement>();
    return <div ref={attach ? ref : undefined} data-testid="probe">{size.width}x{size.height}</div>;
  }

  it("stays 0 by 0 when the browser cannot watch sizes", () => {
    vi.stubGlobal("ResizeObserver", undefined);
    const { getByTestId } = render(<Probe attach />);
    expect(getByTestId("probe").textContent).toBe("0x0");
    vi.unstubAllGlobals();
  });

  it("stays 0 by 0 when nothing is attached to measure", () => {
    const { getByTestId } = render(<Probe attach={false} />);
    expect(getByTestId("probe").textContent).toBe("0x0");
  });
});
