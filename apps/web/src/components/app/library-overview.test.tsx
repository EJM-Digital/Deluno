import { act, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MediaItem } from "../../lib/media-types";

// jsdom has no layout, so the virtualiser is told to show every row (plus one that has no title, which it must skip).
vi.mock("@tanstack/react-virtual", () => ({
  useVirtualizer: ({ count, getScrollElement, estimateSize }: { count: number; getScrollElement: () => unknown; estimateSize: () => number }) => (getScrollElement(), estimateSize(), {
    getTotalSize: () => count * 208,
    getVirtualItems: () => Array.from({ length: count + 1 }, (_, index) => ({ index, start: index * 208 })),
    measureElement: () => undefined
  })
}));
vi.mock("../ui/artwork-image", () => ({ ArtworkImage: ({ src }: { src: string }) => <img data-testid="backdrop" src={src} alt="" /> }));
vi.mock("./library-grid", () => ({ PosterArtwork: ({ title }: { title: string }) => <div data-testid="poster">{title}</div> }));
vi.mock("../ui/title-mark", () => ({ TitleMarkChip: ({ item }: { item: { title: string } }) => <span data-testid="mark">{item.title}</span> }));

const { LibraryOverview } = await import("./library-overview");

const item = (over: Partial<MediaItem> = {}): MediaItem => ({
  id: "m1", type: "movie", title: "Dune", year: 2021, monitored: true, poster: null, backdrop: null, genres: [], overview: "Spice.", quality: null, rating: null, runtimeMinutes: null, ...over
}) as MediaItem;

let observers: Array<{ callback: (entries: Array<{ isIntersecting: boolean }>) => void; observe: ReturnType<typeof vi.fn>; disconnect: ReturnType<typeof vi.fn> }>;

beforeEach(() => {
  observers = [];
  vi.stubGlobal("IntersectionObserver", class {
    observe = vi.fn();
    disconnect = vi.fn();
    constructor(public callback: (entries: Array<{ isIntersecting: boolean }>) => void) { observers.push(this as never); }
  });
});
afterEach(() => vi.unstubAllGlobals());

function mount(items: MediaItem[], over: Partial<Parameters<typeof LibraryOverview>[0]> = {}) {
  const handlers = { onSelect: vi.fn(), onToggle: vi.fn(), onEndReached: vi.fn() };
  render(<MemoryRouter><LibraryOverview items={items} selectedIds={[]} isComplete={false} {...handlers} {...over} /></MemoryRouter>);
  return handlers;
}

describe("the overview layout", () => {
  it("shows a wide row per title with its year, monitoring, synopsis and a link to the title page", () => {
    mount([item(), item({ id: "s1", type: "show", title: "Severance", monitored: false })]);
    expect(screen.getAllByText("Spice.")).toHaveLength(2);
    expect(screen.getByText("Monitored")).toBeTruthy();
    expect(screen.getByText("Unmonitored")).toBeTruthy();
    const links = screen.getAllByRole("link", { name: "Open" });
    expect(links.map((l) => l.getAttribute("href"))).toEqual(["/movies/m1", "/tv/s1"]);
  });

  it("shows runtime, rating, quality, genres (four at most) and the backdrop only when the title has them", () => {
    mount([item({ runtimeMinutes: 155, rating: 8.04, quality: "WEB 1080p", genres: ["Drama", "Sci-Fi", "Epic", "Adventure", "Extra"], backdrop: "/b.jpg", localizedTitle: "Duna" } as Partial<MediaItem>)]);
    expect(screen.getByText("8.0")).toBeTruthy();
    expect(screen.getByText("WEB 1080p")).toBeTruthy();
    expect(screen.getByText("Adventure")).toBeTruthy();
    expect(screen.queryByText("Extra")).toBeNull();
    expect(screen.getByTestId("backdrop")).toBeTruthy();
    expect(screen.getByText("Duna")).toBeTruthy();
    expect(screen.getByText(/2h|155/)).toBeTruthy();
  });

  it("says plainly when a title has no synopsis, and shows a zero rating", () => {
    mount([item({ overview: "   ", rating: 0 })]);
    expect(screen.getByText(/No synopsis has been stored yet/)).toBeTruthy();
    expect(screen.getByText("0.0")).toBeTruthy();
    expect(screen.queryByTestId("backdrop")).toBeNull();
  });

  it("opens a title on a plain click and toggles its selection on Ctrl or Cmd click, from the poster and the name", () => {
    const { onSelect, onToggle } = mount([item()]);
    fireEvent.click(screen.getByRole("button", { name: "Open Dune" }));
    fireEvent.click(screen.getByText("Dune", { selector: "p" }));
    expect(onSelect).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole("button", { name: "Open Dune" }), { ctrlKey: true });
    fireEvent.click(screen.getByText("Dune", { selector: "p" }), { metaKey: true });
    expect(onToggle).toHaveBeenCalledTimes(2);
    expect(onToggle).toHaveBeenCalledWith("m1");
  });

  it("selects with the checkbox, and says Deselect for a selected title", () => {
    const { onToggle } = mount([item(), item({ id: "m2", title: "Arrival" })], { selectedIds: ["m2"] });
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Dune" }));
    expect(onToggle).toHaveBeenCalledWith("m1");
    expect(screen.getByRole("checkbox", { name: "Deselect Arrival" })).toBeTruthy();
  });

  it("opening the link does not also select the row", () => {
    const { onSelect } = mount([item()]);
    fireEvent.click(screen.getByRole("link", { name: "Open" }));
    expect(onSelect).not.toHaveBeenCalled();
  });
});

describe("loading more titles as the end comes into view", () => {
  it("asks for the next slice when the end shows, not when it does not", () => {
    const { onEndReached } = mount([item()]);
    expect(observers[0]!.observe).toHaveBeenCalled();
    act(() => observers[0]!.callback([{ isIntersecting: false }]));
    expect(onEndReached).not.toHaveBeenCalled();
    act(() => observers[0]!.callback([{ isIntersecting: true }]));
    expect(onEndReached).toHaveBeenCalledTimes(1);
  });

  it("stops asking once the library is complete", () => {
    mount([item()], { isComplete: true });
    expect(observers).toHaveLength(0);
  });
});
