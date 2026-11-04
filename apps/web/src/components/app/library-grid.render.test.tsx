import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MediaItem } from "../../lib/media-types";
import { DEFAULT_DISPLAY_PREFERENCES } from "../../lib/display-preferences";
import { PosterArtwork, ProgressiveGrid, posterRowsFor } from "./library-grid";

const virtual = vi.hoisted(() => ({ options: null as null | { count: number; overscan: number; estimateSize: () => number; getScrollElement: () => HTMLElement | null }, rows: [] as number[] | null, measure: vi.fn() }));
vi.mock("@tanstack/react-virtual", () => ({
  useVirtualizer: (options: NonNullable<typeof virtual.options>) => {
    virtual.options = options;
    const indexes = virtual.rows ?? Array.from({ length: options.count }, (_, index) => index);
    return { getVirtualItems: () => indexes.map((index) => ({ index, key: `row-${index}`, start: index * 100 })), getTotalSize: () => options.count * 100, measureElement: virtual.measure };
  }
}));
vi.mock("../ui/title-mark", () => ({
  TitleBars: (props: { showMediaText: boolean; showSubtitleText: boolean; item: { type: string } }) => (
    <i data-testid="bars" data-type={props.item.type} data-media={String(props.showMediaText)} data-subs={String(props.showSubtitleText)} />
  )
}));
const rail = vi.hoisted(() => ({ jumpTo: vi.fn(), args: null as unknown[] | null }));
vi.mock("./library-jump-rail", () => ({
  useJumpRail: (...args: unknown[]) => { rail.args = args; return { slotWidth: 24, activeIndex: 1, jumpTo: rail.jumpTo }; },
  JumpRail: ({ buckets, activeIndex, isComplete, onJump }: { buckets: unknown[]; activeIndex: number; isComplete: boolean; onJump: (index: number) => void }) => (
    <button type="button" onClick={() => onJump(7)}>jump rail {buckets.length} {activeIndex} {String(isComplete)}</button>
  )
}));
const fetcher = vi.hoisted(() => vi.fn());
vi.mock("../../lib/use-auth", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/use-auth")>()), authedFetch: fetcher }));

const observers: Array<{ callback: () => void; observed: Element | null; disconnected: boolean }> = [];
let width = 1000;
let probeWidth: Record<string, number> = { "var(--library-card-sm)": 100, "var(--library-card-md)": 200, "var(--library-card-lg)": 300 };
let gap = "12px";

beforeEach(() => {
  virtual.rows = null;
  observers.length = 0;
  width = 1000;
  gap = "12px";
  vi.stubGlobal("ResizeObserver", class {
    record: (typeof observers)[number];
    constructor(callback: () => void) { this.record = { callback, observed: null, disconnected: false }; observers.push(this.record); }
    observe(element: Element) { this.record.observed = element; }
    disconnect() { this.record.disconnected = true; }
  });
  vi.spyOn(HTMLElement.prototype, "clientWidth", "get").mockImplementation(() => width);
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    const min = /min-width:\s*([^;]+);/.exec(this.getAttribute("style") ?? "")?.[1]?.trim() ?? "";
    return { width: probeWidth[min] ?? 0 } as DOMRect;
  });
  const real = window.getComputedStyle.bind(window);
  vi.spyOn(window, "getComputedStyle").mockImplementation((element: Element, pseudo?: string | null) => {
    const style = real(element, pseudo);
    return new Proxy(style, { get: (target, prop) => (prop === "marginLeft" ? gap : typeof target[prop as keyof CSSStyleDeclaration] === "function" ? (target[prop as keyof CSSStyleDeclaration] as () => unknown).bind(target) : Reflect.get(target, prop)) });
  });
  fetcher.mockReset();
  fetcher.mockResolvedValue({ ok: true, json: async () => ({ releaseName: "Heat.1995", dispatchStatus: "sent", downloadClientName: "qBittorrent" }) });
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); probeWidth = { "var(--library-card-sm)": 100, "var(--library-card-md)": 200, "var(--library-card-lg)": 300 }; });

const movie = (id: string, extra: Partial<MediaItem> = {}): MediaItem => ({
  id, title: `Film ${id}`, year: 2001, type: "movie", poster: null, backdrop: null, quality: "Bluray-1080p", monitored: true, sizeGb: 8, rating: 7.6, genres: ["Drama"], added: "Aug 26", overview: "", hasFile: true, ...extra
}) as MediaItem;
const show = (id: string, extra: Partial<MediaItem> = {}): MediaItem => movie(id, { type: "show", title: `Show ${id}`, ...extra });
const NO_ROWS = {} as never;

interface Over { items?: MediaItem[]; cardSize?: "sm" | "md" | "lg"; displayOptions?: object; selectedIds?: string[]; keyBust?: string; isComplete?: boolean; onSelect?: (item: MediaItem) => void; onToggle?: (id: string) => void; onEndReached?: () => void }
type GridProps = Parameters<typeof ProgressiveGrid>[0];
const behind = vi.fn();
const grid = (over: Over = {}) => {
  behind.mockReset();
  const props = { sortField: "title", sortDirection: "asc", items: [movie("1"), movie("2")], cardSize: "md" as const, displayOptions: { showTitle: true }, selectedIds: [] as string[], keyBust: "a", isComplete: true, onSelect: vi.fn(), onToggle: vi.fn(), onEndReached: vi.fn(), ...over };
  const view = render(<MemoryRouter><div onClick={behind}><ProgressiveGrid {...(props as unknown as GridProps)} /></div></MemoryRouter>);
  return { props, ...view, rerenderWith: (next: Over) => view.rerender(<MemoryRouter><div onClick={behind}><ProgressiveGrid {...({ ...props, ...next } as unknown as GridProps)} /></div></MemoryRouter>) };
};
const template = () => (document.querySelector(".stagger") as HTMLElement).style.gridTemplateColumns;

describe("how many posters fit across", () => {
  it("is the container's width over a poster and its gap, measured on a probe that is gone again", () => {
    grid({ items: Array.from({ length: 9 }, (_, index) => movie(String(index))) });
    expect(template()).toBe("repeat(4, minmax(var(--library-card-md), 1fr))");
    expect(document.querySelectorAll(".stagger")).toHaveLength(3);
    expect(document.querySelector("[style*='visibility']")).toBeNull();
    expect(virtual.options?.count).toBe(3);
  });

  it("measures a probe as wide as the size of card asked for", () => {
    grid({ cardSize: "sm", items: Array.from({ length: 12 }, (_, index) => movie(String(index))) });
    expect(template()).toBe("repeat(9, minmax(var(--library-card-sm), 1fr))");
    cleanup();
    grid({ cardSize: "lg", items: Array.from({ length: 12 }, (_, index) => movie(String(index))) });
    expect(template()).toBe("repeat(3, minmax(var(--library-card-lg), 1fr))");
  });

  it("puts the probe beside the grid, not inside it, and gives it the grid's gap", () => {
    const seen: string[] = [];
    vi.mocked(HTMLElement.prototype.getBoundingClientRect).mockImplementation(function (this: HTMLElement) {
      seen.push(`${this.parentElement?.className.includes("flex") ? "beside" : "other"}|${this.getAttribute("style")}`);
      return { width: 200 } as DOMRect;
    });
    grid();
    expect(seen[0]).toBe("beside|position: absolute; visibility: hidden; pointer-events: none; min-width: var(--library-card-md); margin-left: var(--library-grid-gap);");
  });

  it("is one when the probe cannot be measured, and one when the grid is narrower than a poster", () => {
    probeWidth = {};
    gap = "0px";
    grid();
    expect(template()).toBe("repeat(1, minmax(var(--library-card-md), 1fr))");
    cleanup();
    probeWidth = { "var(--library-card-md)": 200 };
    width = 100;
    gap = "12px";
    grid();
    expect(template()).toBe("repeat(1, minmax(var(--library-card-md), 1fr))");
  });

  it("counts a gap it cannot read as no gap", () => {
    gap = "";
    width = 600;
    grid({ items: Array.from({ length: 9 }, (_, index) => movie(String(index))) });
    expect(template()).toBe("repeat(3, minmax(var(--library-card-md), 1fr))");
  });

  it("keeps the last real answer while the container measures nothing, and measures again when it is resized", () => {
    width = 0;
    grid({ items: Array.from({ length: 9 }, (_, index) => movie(String(index))) });
    expect(template()).toBe("repeat(4, minmax(var(--library-card-md), 1fr))");
    width = 600;
    act(() => observers[0].callback());
    expect(template()).toBe("repeat(2, minmax(var(--library-card-md), 1fr))");
    expect(observers[0].observed).toBe(document.querySelector(".overflow-auto"));
  });

  it("measures a container that has been taken out of the page by putting its probe in the container itself", () => {
    grid({ items: Array.from({ length: 9 }, (_, index) => movie(String(index))) });
    const callback = observers[0].callback;
    document.querySelector(".overflow-auto")!.remove();
    expect(() => callback()).not.toThrow();
    expect(document.querySelector("[style*='visibility']")).toBeNull();
  });

  it("stops watching when it goes away, and watches the new container when the grid is rebuilt", () => {
    const view = grid({ keyBust: "a" });
    const first = observers[0];
    view.rerenderWith({ keyBust: "b" });
    expect(first.disconnected).toBe(true);
    expect(observers.at(-1)?.disconnected).toBe(false);
    view.unmount();
    expect(observers.at(-1)?.disconnected).toBe(true);
  });
});

describe("the virtual shelf", () => {
  it("sizes its rows by the size of card, keeps three beyond the screen, and scrolls the container", () => {
    grid({ cardSize: "sm" });
    expect(virtual.options?.estimateSize()).toBe(245);
    expect(virtual.options?.overscan).toBe(3);
    expect(virtual.options?.getScrollElement()).toBe(document.querySelector(".overflow-auto"));
    cleanup();
    grid({ cardSize: "md" });
    expect(virtual.options?.estimateSize()).toBe(340);
    cleanup();
    grid({ cardSize: "lg" });
    expect(virtual.options?.estimateSize()).toBe(440);
  });

  it("draws only the rows the virtualiser asks for, each at its place and with its own measuring", () => {
    virtual.rows = [1];
    grid({ items: Array.from({ length: 9 }, (_, index) => movie(String(index))) });
    const rows = document.querySelectorAll<HTMLElement>("[data-index]");
    expect(rows).toHaveLength(1);
    expect(rows[0].dataset.index).toBe("1");
    expect(rows[0].style.transform).toBe("translateY(100px)");
    expect(within(rows[0]).getAllByRole("button", { name: /^Open Film/ }).map((button) => button.getAttribute("aria-label"))).toEqual(["Open Film 4", "Open Film 5", "Open Film 6", "Open Film 7"]);
    expect((document.querySelector(".overflow-auto > div") as HTMLElement).style.height).toBe("300px");
    expect(virtual.measure).toHaveBeenCalled();
    expect((document.querySelector(".overflow-auto > div") as HTMLElement).style.position).toBe("relative");
  });

  it("asks for more again when the rows change, and re-cuts the jump rail's stops for the items it has now", () => {
    const view = grid({ items: Array.from({ length: 12 }, (_, index) => movie(String(index))) });
    const asked = view.props.onEndReached as ReturnType<typeof vi.fn>;
    const before = asked.mock.calls.length;
    const firstStops = (rail.args?.[3] as unknown[]).length;
    view.rerenderWith({ items: Array.from({ length: 40 }, (_, index) => movie(String(index))) });
    expect(asked.mock.calls.length).toBeGreaterThan(before);
    const stops = rail.args?.[3] as unknown[];
    view.rerenderWith({ items: [movie("a", { title: "Aardvark" }), movie("b", { title: "Mango" }), movie("c", { title: "Zebra" })] });
    expect(JSON.stringify(rail.args?.[3])).not.toBe(JSON.stringify(stops));
    expect((rail.args?.[3] as unknown[]).length).toBeGreaterThanOrEqual(firstStops);
    expect(rail.args?.[3]).not.toBe(undefined);
  });

  it("asks for more when the last row drawn is one of the last two", () => {
    const view = grid({ items: Array.from({ length: 12 }, (_, index) => movie(String(index))) });
    expect(view.props.onEndReached).toHaveBeenCalled();
  });

  it("asks for more on the second-last row and not on the one before it", () => {
    const items = Array.from({ length: 20 }, (_, index) => movie(String(index)));
    virtual.rows = [2];
    const near = grid({ items });
    expect(near.props.onEndReached).not.toHaveBeenCalled();
    cleanup();
    virtual.rows = [3];
    const last = grid({ items });
    expect(last.props.onEndReached).toHaveBeenCalled();
  });

  it("does not ask for more when nothing is drawn", () => {
    virtual.rows = [];
    const view = grid({ items: [] });
    expect(view.props.onEndReached).not.toHaveBeenCalled();
  });

  it("hands the jump rail its columns, rows and stops, in the slot beside the shelf", () => {
    const view = grid({ isComplete: false });
    expect(rail.args?.[1]).toBe(4);
    expect(screen.getByRole("button", { name: /^jump rail \d+ 1 false$/ }).parentElement).toHaveStyle({ width: "24px" });
    fireEvent.click(screen.getByRole("button", { name: /jump rail/ }));
    expect(rail.jumpTo).toHaveBeenCalledWith(7);
    expect(view.props.onEndReached).toBeDefined();
  });
});

describe("a poster", () => {
  it("opens its title when clicked, from anywhere on it", () => {
    const view = grid();
    fireEvent.click(screen.getByRole("button", { name: "Open Film 1" }));
    expect(view.props.onSelect).toHaveBeenCalledWith(view.props.items[0]);
  });

  it("is selected and deselected by its round toggle, without opening the title", () => {
    const view = grid({ selectedIds: ["2"] });
    fireEvent.click(screen.getByRole("button", { name: "Select Film 1" }));
    expect(view.props.onToggle).toHaveBeenCalledWith("1");
    expect(behind).not.toHaveBeenCalled();
    expect(view.props.onSelect).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Deselect Film 2" }));
    expect(view.props.onToggle).toHaveBeenLastCalledWith("2");
  });

  it("keeps the look of a poster: its toggle, its artwork box, its title, its rows and its round actions", () => {
    grid({ selectedIds: ["2"], displayOptions: { showTitle: true, showRating: true }, items: [movie("1", { rating: 7.6 }), movie("2")] });
    const toggle = screen.getByRole("button", { name: "Select Film 1" });
    expect(toggle).toHaveClass("absolute", "left-2", "top-2", "z-10", "flex", "shrink-0", "items-center", "justify-center", "rounded-full", "transition-all", "duration-200");
    expect(screen.getByRole("button", { name: "Deselect Film 2" }).className).toContain("hsl(var(--primary)/0.6)");
    const art = document.querySelector(".aspect-2\\/3") as HTMLElement;
    expect(art).toHaveClass("pointer-events-none", "relative", "z-2", "overflow-hidden", "rounded-xl", "bg-muted", "transition-all", "duration-300", "shadow-card", "group-hover:-translate-y-1", "group-hover:shadow-lg");
    expect(screen.getByText("Film 1")).toHaveClass("h-lh", "truncate", "font-semibold", "text-foreground", "max-md:h-auto", "max-md:whitespace-normal", "max-md:break-words");
    expect(screen.getAllByTitle("Rating: 7.6")[0].querySelector("svg")).toHaveClass("h-3", "w-3", "shrink-0");
    const search = screen.getAllByRole("button", { name: "Search for Film 1 now" })[0];
    expect(search).toHaveClass("pointer-events-auto", "inline-flex", "h-7", "w-7", "items-center", "justify-center", "rounded-full", "bg-black/55", "text-white", "ring-1", "ring-inset", "ring-white/25", "backdrop-blur-xs", "transition", "hover:bg-primary", "hover:text-primary-foreground", "hover:ring-primary", "focus-visible:outline-hidden", "focus-visible:ring-2", "focus-visible:ring-primary");
    expect(search.querySelector("svg")).toHaveClass("h-3.5", "w-3.5");
  });

  it("keeps the toggle's classes apart, selected and not", () => {
    grid({ selectedIds: ["2"] });
    const selected = screen.getByRole("button", { name: "Deselect Film 2" }).className.split(/\s+/);
    const plain = screen.getByRole("button", { name: "Select Film 1" }).className.split(/\s+/);
    expect(selected).toEqual(expect.arrayContaining(["opacity-100", "scale-100", "text-primary-foreground"]));
    expect(plain).toEqual(expect.arrayContaining(["opacity-0", "scale-90", "group-hover:opacity-100", "group-hover:scale-100", "backdrop-blur-md"]));
    expect(plain).not.toContain("scale-100");
  });

  it("looks different when selected: a ring, a scrim and a checked toggle", () => {
    grid({ selectedIds: ["2"] });
    const selected = screen.getByRole("button", { name: "Deselect Film 2" });
    const plain = screen.getByRole("button", { name: "Select Film 1" });
    const cards = Array.from(document.querySelectorAll(".group"));
    expect(cards[0].querySelector(".from-primary\\/15")).toBeNull();
    expect(cards[1].querySelector(".from-primary\\/15")).not.toBeNull();
    expect(cards[0].querySelector(".ring-0")).not.toBeNull();
    expect(cards[1].querySelector(".ring-0")).toBeNull();
    expect(selected.className).toContain("opacity-100");
    expect(plain.className).toContain("opacity-0");
    expect(selected.querySelector("svg")).not.toHaveClass("opacity-60");
    expect(plain.querySelector("svg")).toHaveClass("opacity-60");
    expect(document.querySelectorAll(".ring-primary\\/90")).toHaveLength(1);
    expect(document.querySelectorAll(".from-primary\\/15")).toHaveLength(1);
  });

  it("makes the toggle smaller on a small poster", () => {
    grid({ cardSize: "sm" });
    expect(screen.getByRole("button", { name: "Select Film 1" }).className).toContain("h-5 w-5");
    cleanup();
    grid({ cardSize: "md" });
    expect(screen.getByRole("button", { name: "Select Film 1" }).className).toContain("h-6 w-6");
  });

  it("shows its title, in the localised form when there is one, and none when the title is switched off", () => {
    grid({ items: [movie("1", { localizedTitle: "Film Un" } as never), movie("2")] });
    expect(screen.getByText("Film Un")).toHaveAttribute("title", "Film Un");
    expect(screen.getByText("Film 2")).toHaveAttribute("title", "Film 2");
    cleanup();
    grid({ displayOptions: { showTitle: false } });
    expect(screen.queryByText("Film 1")).toBeNull();
  });

  it("sizes its title by the size of card", () => {
    grid({ cardSize: "lg" });
    expect(screen.getByText("Film 1").className).toContain("--library-title-lg");
    cleanup();
    grid({ cardSize: "sm" });
    expect(screen.getByText("Film 1").className).toContain("--library-title-sm");
  });

  it("passes the switches for the words on its bar: quality for a film, episodes for a show, subtitles for both, each on unless turned off", () => {
    const bars = () => Array.from(document.querySelectorAll<HTMLElement>("[data-testid='bars']")).map((bar) => [bar.dataset.type, bar.dataset.media, bar.dataset.subs]);
    grid({ items: [movie("1"), show("2")], displayOptions: { showTitle: true } });
    expect(bars()).toEqual([["movie", "true", "true"], ["show", "true", "true"]]);
    cleanup();
    grid({ items: [movie("1"), show("2")], displayOptions: { showTitle: true, showQualityOnBar: false } });
    expect(bars()).toEqual([["movie", "false", "true"], ["show", "true", "true"]]);
    cleanup();
    grid({ items: [movie("1"), show("2")], displayOptions: { showTitle: true, showEpisodeCountOnBar: false } });
    expect(bars()).toEqual([["movie", "true", "true"], ["show", "false", "true"]]);
    cleanup();
    grid({ items: [movie("1"), show("2")], displayOptions: { showTitle: true, showSubtitleCountOnBar: false } });
    expect(bars()).toEqual([["movie", "true", "false"], ["show", "true", "false"]]);
  });

  it("draws the bar of its state with the switches for its words", () => {
    const view = grid({ items: [movie("1"), show("2")], displayOptions: { showTitle: true, showQualityOnBar: false, showEpisodeCountOnBar: false, showSubtitleCountOnBar: false } });
    expect(view.container.querySelectorAll("[data-title-bars], [class*='title-bar']").length).toBeGreaterThanOrEqual(0);
    expect(screen.getByRole("button", { name: "Open Show 2" })).toBeInTheDocument();
  });

  it("opens the film page or the show page from its Open button", () => {
    const view = grid({ items: [movie("1"), show("2")] });
    const open = screen.getAllByRole("link", { name: "Open" });
    expect(open.map((link) => link.getAttribute("href"))).toEqual(["/movies/1", "/tv/2"]);
    expect(open[0]).toHaveAttribute("title", "Open");
    fireEvent.click(open[0]);
    expect(behind).not.toHaveBeenCalled();
  });
});

describe("what a poster says under the artwork", () => {
  const options = { showTitle: true, showRating: true, showSize: true };

  it("carries its rows on a medium and a large card, and none on a small one", () => {
    grid({ displayOptions: options, cardSize: "md" });
    expect(screen.getAllByTitle("Rating: 7.6")).toHaveLength(2);
    cleanup();
    grid({ displayOptions: options, cardSize: "lg" });
    expect(screen.getAllByTitle("Rating: 7.6")).toHaveLength(2);
    cleanup();
    grid({ displayOptions: options, cardSize: "sm" });
    expect(screen.queryAllByTitle("Rating: 7.6")).toHaveLength(0);
  });

  it("draws a row for every switch that is on, with a dash and a plain tooltip where this title has nothing", () => {
    grid({ displayOptions: options, items: [movie("1", { rating: null, sizeGb: 8 })] });
    const missing = screen.getByTitle("Rating: nothing known for this title");
    expect(missing.querySelector("span")).toHaveTextContent("—");
    expect(missing.querySelector("span")).toHaveClass("opacity-40");
    expect(missing.querySelector("svg")).toHaveClass("opacity-30");
    const present = screen.getByTitle("Size on disk: 8.0 GB");
    expect(present.querySelector("span")).toHaveTextContent("8.0 GB");
    expect(present.querySelector("span")).not.toHaveClass("opacity-40");
    expect(present.querySelector("svg")).toHaveClass("opacity-70");
  });

  it("sizes the title's text by the medium card too", () => {
    grid({ cardSize: "md" });
    expect(screen.getByText("Film 1").className).toContain("--library-title-md");
  });

  it("draws its rows dimmed when empty, and plain when it has the fact", () => {
    grid({ displayOptions: options, items: [movie("1", { rating: 7.6, sizeGb: null })] });
    expect(screen.getByTitle("Rating: 7.6").querySelector("span")!.className).toBe("truncate");
    expect(screen.getByTitle("Size on disk: nothing known for this title").querySelector("span")!.className).toBe("truncate opacity-40");
  });

  it("draws no rows when no switch is on", () => {
    grid({ displayOptions: { showTitle: true } });
    expect(document.querySelector("[title*=':']")).toBeNull();
  });
});

describe("the rows a title has", () => {
  const full = movie("1", { runtimeMinutes: 164, genres: ["Drama", "Crime", "Thriller"], releaseGroup: "SPARKS", codec: "x265", audioCodec: "DTS", added: "2026-01-05T00:00:00Z", tmdbRating: 7.64, imdbRating: 8.1, tomatoRating: 93.4, metacriticRating: 71.2, inCinemas: "2020-03-12T00:00:00Z", digitalRelease: "not a date", physicalRelease: null } as never);
  const all = { showRating: true, showSize: true, showRuntime: true, showGenres: true, showReleaseGroup: true, showCodec: true, showAdded: true, showRatingtmdb: true, showRatingimdb: true, showRatingrottentomatoes: true, showRatingmetacritic: true, showInCinemas: false, showDigitalRelease: true, showPhysicalRelease: true, showEpisodeProgress: true, showNextAiring: true };
  const read = (item: MediaItem, options: object = all) => Object.fromEntries(posterRowsFor(item, options as never).map((row) => [row.label, row.value]));

  it("reads each fact the way it is printed", () => {
    const rows = read(full);
    expect(rows["Rating"]).toBe("7.6");
    expect(rows["Size on disk"]).toBe("8.0 GB");
    expect(rows["Runtime"]).toMatch(/2/);
    expect(rows["Genres"]).toBe("Drama, Crime");
    expect(rows["Release group"]).toBe("SPARKS");
    expect(rows["Codec"]).toBe("x265 · DTS");
    expect(rows["Added to your library"]).toMatch(/Jan|5/);
    expect(rows["TMDb score"]).toBe("TMDb 7.6");
    expect(rows["IMDb score"]).toBe("IMDb 8.1");
    expect(rows["RT score"]).toBe("RT 93%");
    expect(rows["Metacritic score"]).toBe("Metacritic 71%");
  });

  it("prints dates in the person's own date format and time zone, and never as Today or Tomorrow", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-31T12:00:00Z"));
    const iso = { ...DEFAULT_DISPLAY_PREFERENCES, shortDateFormat: "iso" as const, showRelativeDates: true, timezone: "Pacific/Auckland" };
    const rows = Object.fromEntries(posterRowsFor(movie("1", { added: "2026-10-31T10:00:00Z", digitalRelease: "2026-10-31T09:00:00Z", nextAirDateUtc: "2026-12-25T11:00:00Z" } as never), { showAdded: true, showDigitalRelease: true, showNextAiring: true } as never, iso).map((row) => [row.label, row.value]));
    expect(rows["Added to your library"]).toBe("2026-10-31");
    expect(rows["Digital"]).toBe("2026-10-31");
    expect(rows["Next airing"]).toBe("2026-12-26");
    vi.useRealTimers();
  });

  it("prints a date as a date, never as Today or Yesterday, and a release date the same way", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-31T12:00:00Z"));
    const today = read(movie("1", { added: "2026-10-31T10:00:00Z", digitalRelease: "2026-10-31T08:00:00Z", inCinemas: "2026-10-31T08:00:00Z" } as never), { showAdded: true, showDigitalRelease: true, showInCinemas: true });
    for (const value of [today["Added to your library"], today["Digital"], today["Cinemas"]]) expect(value).toMatch(/\d{2}[/-]\d{2}[/-]\d{2,4}|\d{4}-\d{2}-\d{2}/);
    expect(read(full, all)["Cinemas"]).toBeUndefined();
    expect(read(full, { ...all, showInCinemas: true })["Cinemas"]).not.toContain("T00");
    vi.useRealTimers();
  });

  it("prints a release date it cannot read as it arrived, and none it does not have", () => {
    const rows = read(full);
    expect(rows["Digital"]).toBe("not a date");
    expect(rows["Disc"]).toBeNull();
    expect(rows["Cinemas"]).toBeUndefined();
    expect(read(full, { ...all, showInCinemas: true })["Cinemas"]).toMatch(/Mar|12/);
  });

  it("says one codec when only one is known, and none when neither is", () => {
    expect(read(movie("1", { codec: "x265", audioCodec: null } as never))["Codec"]).toBe("x265");
    expect(read(movie("1", { codec: null, audioCodec: "DTS" } as never))["Codec"]).toBe("DTS");
    expect(read(movie("1", { codec: null, audioCodec: null } as never))["Codec"]).toBeNull();
  });

  it("has no size for a title with no file, and no genres, group, runtime or date it does not hold", () => {
    const bare = read(movie("1", { hasFile: false, sizeGb: 8, genres: [], releaseGroup: undefined, runtimeMinutes: 0, added: "", rating: undefined } as never));
    expect(bare["Size on disk"]).toBeNull();
    expect(bare["Genres"]).toBeNull();
    expect(bare["Release group"]).toBeNull();
    expect(bare["Runtime"]).toBeNull();
    expect(bare["Added to your library"]).toBeNull();
    expect(bare["Rating"]).toBeNull();
    expect(read(movie("1", { hasFile: true, sizeGb: null }))["Size on disk"]).toBeNull();
    expect(read(movie("1", { hasFile: undefined, sizeGb: 2 } as never))["Size on disk"]).toBe("2.0 GB");
    expect(read(movie("1", { rating: 0 }))["Rating"]).toBe("0.0");
  });

  it("counts the episodes a show holds over what has aired", () => {
    expect(read(show("1", { airedEpisodeCount: 10, airedWithFileCount: 7 } as never))["Episodes held"]).toBe("7/10 episodes");
    expect(read(show("1", { airedEpisodeCount: 1, airedWithFileCount: 1 } as never))["Episodes held"]).toBe("1/1 episodes");
    expect(read(show("1", { airedEpisodeCount: 10 } as never))["Episodes held"]).toBe("0/10 episodes");
    expect(read(show("1", { airedEpisodeCount: 0, airedWithFileCount: 0 } as never))["Episodes held"]).toBeNull();
    expect(read(show("1", {}))["Episodes held"]).toBeNull();
  });

  it("names the weekday of an episode this week, the date beyond it, and nothing for one that has passed or cannot be read", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-31T00:00:00Z"));
    const next = (value: string | null) => read(show("1", { nextAirDateUtc: value } as never))["Next airing"];
    expect(next("2026-11-03T12:00:00Z")).toMatch(/Mon|Tue|Tuesday|Monday/);
    expect(next("2026-12-25T12:00:00Z")).toMatch(/25|Dec/);
    expect(next("2026-10-30T12:00:00Z")).toBeNull();
    expect(next("garbage")).toBeNull();
    expect(next(null)).toBeNull();
    expect(next("2026-11-06T23:00:00Z")).toMatch(/Fri|Sat|Friday|Saturday/);
    expect(next("2026-11-07T01:00:00Z")).toMatch(/7|Nov/);
    expect(next("2026-10-31T00:00:00Z")).not.toBeNull();
    expect(next("2026-10-31T00:00:00Z")).toMatch(/Sat|Saturday/);
    expect(next("2026-11-07T00:00:00Z")).toMatch(/\d/);
    expect(next("2026-11-07T00:00:00Z")).not.toMatch(/Sat|Saturday/);
    expect(next("2026-10-31T00:00:00Z")).not.toBe(next("2026-11-07T00:00:00Z"));
    vi.setSystemTime(new Date("2026-10-31T00:00:00Z"));
    expect(next("2026-11-03T00:00:00Z")).not.toMatch(/Today|Tomorrow|in /);
  });
});

describe("the poster's artwork", () => {
  it("shows the picture with the title as its name, or a placeholder with the title when there is none", () => {
    const { container } = render(<PosterArtwork src="/p.jpg" title="Heat" className="x" />);
    expect(container.querySelector("img")).toHaveAttribute("alt", "Heat");
    expect(container.querySelector("img")).toHaveAttribute("loading", "lazy");
    expect(container.querySelector("img")?.className).toContain("object-cover");
    cleanup();
    const plain = render(<PosterArtwork src={null} title="Heat" />);
    const roomy = plain.container.innerHTML;
    plain.unmount();
    const explicit = render(<PosterArtwork src={null} title="Heat" compact={false} />);
    expect(explicit.container.innerHTML).toBe(roomy);
    explicit.unmount();
    const small = render(<PosterArtwork src={null} title="Heat" compact />);
    expect(small.container.innerHTML).not.toBe(roomy);
    expect(screen.getByText(/Heat|H/)).toBeInTheDocument();
  });
});

describe("searching and refreshing from a poster", () => {
  const press = (name: string) => fireEvent.click(screen.getByRole("button", { name }));

  it("searches for a film automatically and says what it did", async () => {
    grid();
    press("Search for Film 1 now");
    await waitFor(() => expect(screen.getByRole("status", { name: "Result for Film 1" })).toHaveTextContent("Sent to qBittorrent"));
    expect(fetcher).toHaveBeenCalledWith("/api/movies/1/search", { method: "POST" });
  });

  it("searches for a show through its own endpoint, and tells whether it already had a file", async () => {
    fetcher.mockResolvedValue({ ok: true, json: async () => ({}) });
    grid({ items: [show("2", { hasFile: true }), show("3", { hasFile: false })] });
    press("Search for Show 2 now");
    await waitFor(() => expect(screen.getByRole("status", { name: "Result for Show 2" })).toHaveTextContent("Nothing better found"));
    expect(fetcher).toHaveBeenCalledWith("/api/series/2/search", { method: "POST" });
    press("Search for Show 3 now");
    await waitFor(() => expect(screen.getByRole("status", { name: "Result for Show 3" })).toHaveTextContent("Nothing found yet"));
  });

  it("says a search finished when the answer cannot be read", async () => {
    fetcher.mockResolvedValue({ ok: true, json: async () => { throw new Error("bad json"); } });
    grid();
    press("Search for Film 1 now");
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Search finished"));
  });

  it("refreshes the metadata and says so", async () => {
    grid({ items: [show("2")] });
    press("Refresh metadata for Show 2");
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Metadata refreshed"));
    expect(fetcher).toHaveBeenCalledWith("/api/series/2/metadata/refresh", { method: "POST" });
  });

  it("says a search failed, whether the server refused or could not be reached", async () => {
    fetcher.mockResolvedValueOnce({ ok: false });
    grid();
    press("Search for Film 1 now");
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Search failed"));
    fetcher.mockRejectedValueOnce(new Error("offline"));
    press("Search for Film 2 now");
    await waitFor(() => expect(screen.getByRole("status", { name: "Result for Film 2" })).toHaveTextContent("Search failed"));
  });

  it("says a refresh failed", async () => {
    fetcher.mockResolvedValueOnce({ ok: false });
    grid();
    press("Refresh metadata for Film 1");
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Refresh failed"));
    fetcher.mockRejectedValueOnce(new Error("offline"));
    press("Refresh metadata for Film 2");
    await waitFor(() => expect(screen.getByRole("status", { name: "Result for Film 2" })).toHaveTextContent("Refresh failed"));
  });

  it("holds the button while it works, and ignores the other action until it is done", async () => {
    let finish: (value: unknown) => void = () => undefined;
    fetcher.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    grid();
    press("Search for Film 1 now");
    const search = screen.getByRole("button", { name: "Search for Film 1 now" });
    await waitFor(() => expect(search).toBeDisabled());
    expect(search.className).toContain("opacity-60");
    expect(search.querySelector("svg")).toHaveClass("animate-spin");
    expect(screen.getByRole("button", { name: "Refresh metadata for Film 1" })).not.toBeDisabled();
    press("Refresh metadata for Film 1");
    expect(fetcher).toHaveBeenCalledTimes(1);
    await act(async () => { finish({ ok: true, json: async () => ({}) }); });
    await waitFor(() => expect(search).not.toBeDisabled());
    expect(search.className).not.toContain("opacity-60");
    expect(search.querySelector("svg")).not.toHaveClass("animate-spin");
  });

  it("holds the Refresh button, and not Search, while a refresh works", async () => {
    let finish: (value: unknown) => void = () => undefined;
    fetcher.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    grid();
    press("Refresh metadata for Film 1");
    const refresh = screen.getByRole("button", { name: "Refresh metadata for Film 1" });
    await waitFor(() => expect(refresh).toBeDisabled());
    expect(refresh.className).toContain("opacity-60");
    expect(refresh.querySelector("svg")).toHaveClass("animate-spin");
    expect(screen.getByRole("button", { name: "Search for Film 1 now" })).not.toBeDisabled();
    expect(screen.getByRole("button", { name: "Search for Film 1 now" }).className).not.toContain("opacity-60");
    await act(async () => { finish({ ok: true, json: async () => ({}) }); });
    await waitFor(() => expect(refresh).not.toBeDisabled());
  });

  it("never shows the Open link as busy", () => {
    grid();
    const open = screen.getAllByRole("link", { name: "Open" })[0];
    expect(open.className).not.toContain("opacity-60");
    expect(open.querySelector("svg")).not.toHaveClass("animate-spin");
  });

  it("clears the answer after six seconds, and an action does not open the title", async () => {
    vi.useFakeTimers();
    const view = grid();
    press("Search for Film 1 now");
    await act(async () => { await vi.advanceTimersByTimeAsync(10); });
    expect(screen.getByRole("status")).toBeInTheDocument();
    expect(behind).not.toHaveBeenCalled();
    expect(view.props.onSelect).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(5900); });
    expect(screen.getByRole("status")).toBeInTheDocument();
    await act(async () => { await vi.advanceTimersByTimeAsync(200); });
    expect(screen.queryByRole("status")).toBeNull();
    void NO_ROWS;
  });

  it("clears the last answer when it starts again", async () => {
    let finish: (value: unknown) => void = () => undefined;
    grid();
    press("Refresh metadata for Film 1");
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Metadata refreshed"));
    fetcher.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    press("Search for Film 1 now");
    await waitFor(() => expect(screen.queryByRole("status")).toBeNull());
    await act(async () => { finish({ ok: true, json: async () => ({}) }); });
  });
});
