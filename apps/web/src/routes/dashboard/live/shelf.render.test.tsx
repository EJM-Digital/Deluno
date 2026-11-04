import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { arrivedWhen, SHELF_LIBRARY_KEY, Shelf } from "./shelf";
import { TILE_KEY_ATTRIBUTE, useSlideNeighbours, useTileArrivals } from "./shelf-motion";
import { useOpenSlots } from "./shelf-slots";
import { shelfRowNeed } from "./layout";
import { NO_TITLES } from "./titles";

// The shelf's box, which a test sets: jsdom measures nothing.
let box = { width: 900, height: 330 };
vi.mock("./hooks", async (importOriginal) => {
  const { createRef } = await import("react");
  return { ...(await importOriginal<typeof import("./hooks")>()), useElementSize: () => [createRef(), box] };
});

vi.mock("./shelf-motion", async (importOriginal) => {
  const real = await importOriginal<typeof import("./shelf-motion")>();
  return { ...real, useSlideNeighbours: vi.fn(real.useSlideNeighbours), useTileArrivals: vi.fn(real.useTileArrivals) };
});
vi.mock("./shelf-slots", async (importOriginal) => {
  const real = await importOriginal<typeof import("./shelf-slots")>();
  return { ...real, useOpenSlots: vi.fn(real.useOpenSlots) };
});

beforeEach(() => { localStorage.clear(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); box = { width: 900, height: 330 }; });

const NOW = new Date("2026-10-31T12:00:00Z").getTime();
const item = (key: string, extra: object = {}) => ({ key, entity: { kind: "movie", id: key }, fallbackTitle: `Film ${key} (2001)`, release: null, quality: "Bluray 1080p", previousQuality: null, upgraded: false, sizeBytes: null, at: new Date().toISOString(), requestedBy: null, to: `/movies/${key}`, qualityMet: null, libraryId: null, libraryName: null, ...extra }) as never;
const counts = (libraryId: string, libraryName: string) => ({ libraryId, libraryName, mediaType: "movies", met: 1, upgradable: 1, downloading: 0, missing: 0, arrivedToday: 1 });
const section = { movies: { qualityMet: 1, upgradable: 1, downloading: 0, missing: 0 }, shows: { qualityMet: 0, upgradable: 0, downloading: 0, missing: 0 }, libraryCounts: [counts("hd", "Movies HD"), counts("kids", "Kids")] } as never;
const show = (items: unknown[], extra: Partial<Parameters<typeof Shelf>[0]> = {}) =>
  render(<MemoryRouter><Shelf items={items as never[]} titles={NO_TITLES} newest={new Set()} heading="arrived today" {...extra} /></MemoryRouter>);

describe("when a title arrived", () => {
  it("says minutes ago for today, then yesterday, then days by calendar day", () => {
    expect(arrivedWhen("2026-10-31T11:48:00Z", NOW)).toBe("12 min ago");
    expect(arrivedWhen("2026-10-30T23:00:00", new Date("2026-10-31T01:00:00").getTime())).toBe("yesterday");
    expect(arrivedWhen("2026-10-28T09:00:00", new Date("2026-10-31T01:00:00").getTime())).toBe("3 days ago");
  });

  it("says never for a time it cannot read", () => {
    expect(arrivedWhen("not a time", NOW)).toBe("never");
  });
});

describe("the shelf's tooltip and name for a poster", () => {
  it("say an upgrade without its old quality, a requester, and an episode's number", () => {
    show([item("1", { upgraded: true, entity: { kind: "series", id: "s1" }, release: "Show.S01E02.1080p-GRP", requestedBy: "Sam" })]);
    const poster = screen.getByRole("link", { name: /^Film 1/ });
    expect(poster.getAttribute("title")).toContain("an upgrade");
    expect(poster.getAttribute("title")).toContain("requested by Sam");
    expect(poster.getAttribute("aria-label")).toContain("Film 1 (2001) S01E02: in the library, an upgrade, ");
  });

  it("count the upgrades of a show that had several episodes arrive, and name no requester", () => {
    show([
      item("1", { upgraded: true, entity: { kind: "series", id: "s1" }, release: "Show.S01E02.1080p-GRP", requestedBy: "Sam" }),
      item("2", { upgraded: false, entity: { kind: "series", id: "s1" }, release: "Show.S01E03.1080p-GRP", requestedBy: "Sam" })
    ]);
    const poster = screen.getAllByRole("link").find((link) => link.getAttribute("title")?.includes("1 upgraded"))!;
    expect(poster.getAttribute("title")).not.toContain("requested by");
    expect(poster.getAttribute("aria-label")).toContain("2 episodes: in the library, 1 upgraded");
  });

  it("say whether the quality is met or can still be upgraded", () => {
    show([item("1", { qualityMet: true }), item("2", { qualityMet: false })]);
    expect(screen.getByRole("link", { name: /^Film 1/ }).getAttribute("title")).toContain("quality met");
    expect(screen.getByRole("link", { name: /^Film 2/ }).getAttribute("title")).toContain("can still be upgraded");
  });
});

describe("the tiny caption", () => {
  it("is the status line alone, in the shortest words that fit under the poster", () => {
    box = { width: 340, height: 120 };
    show([item("1", { upgraded: true })]);
    const caption = document.querySelector<HTMLElement>(".lsh-cap");
    expect(caption).toHaveAttribute("data-tier", "tiny");
    expect(caption!.querySelector(".lsh-title")).toBeNull();
    expect(caption!.querySelector(".lsh-status")).not.toBeNull();
  });
});

describe("the library chips", () => {
  it("remember the chosen library in this browser, and All forgets it", async () => {
    show([item("1", { libraryId: "kids", libraryName: "Kids" }), item("2", { libraryId: "hd", libraryName: "Movies HD" })], { library: section });
    const user = userEvent.setup();
    await user.click(screen.getByRole("radio", { name: "Kids" }));
    expect(localStorage.getItem(SHELF_LIBRARY_KEY)).toBe("kids");
    expect(screen.queryByRole("link", { name: /^Film 2/ })).toBeNull();
    await user.click(screen.getByRole("radio", { name: "All" }));
    expect(localStorage.getItem(SHELF_LIBRARY_KEY)).toBeNull();
    expect(screen.getByRole("link", { name: /^Film 2/ })).toBeInTheDocument();
  });

  it("start on the remembered library", () => {
    localStorage.setItem(SHELF_LIBRARY_KEY, "hd");
    show([item("1", { libraryId: "kids", libraryName: "Kids" }), item("2", { libraryId: "hd", libraryName: "Movies HD" })], { library: section });
    expect(screen.queryByRole("link", { name: /^Film 1/ })).toBeNull();
    expect(screen.getByRole("link", { name: /^Film 2/ })).toBeInTheDocument();
  });

  it("still work when the browser will not keep the choice", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
    show([item("1", { libraryId: "kids", libraryName: "Kids" }), item("2", { libraryId: "hd", libraryName: "Movies HD" })], { library: section });
    expect(screen.getByRole("link", { name: /^Film 2/ })).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole("radio", { name: "Kids" }));
    expect(screen.queryByRole("link", { name: /^Film 2/ })).toBeNull();
  });
});

describe("a poster's library tag", () => {
  it("opens the library without opening the poster behind it", () => {
    const behind = vi.fn();
    render(<MemoryRouter><div onClick={behind}><Shelf items={[item("1", { libraryId: "kids", libraryName: "Kids" })]} titles={NO_TITLES} newest={new Set()} heading="arrived today" library={section} /></div></MemoryRouter>);
    fireEvent.click(screen.getByRole("link", { name: "Kids" }));
    expect(behind).not.toHaveBeenCalled();
  });
});

describe("the height the shelf needs", () => {
  it("is told to the page once the row is measured, from the card's chrome and the posters' size", () => {
    const sizes: Array<[string, number]> = [["clientHeight", 200], ["clientWidth", 900], ["offsetHeight", 260]];
    for (const [name, value] of sizes) vi.spyOn(HTMLElement.prototype, name as "clientHeight", "get").mockReturnValue(value);
    const need = vi.fn();
    show([item("1")], { onHeightNeed: need });
    expect(need).toHaveBeenCalledTimes(1);
    expect(need.mock.calls[0][0]).toBeGreaterThan(60);
  });
});

describe("a poster's words and marks", () => {
  it("has a tooltip of everything known about it, in order, and a plain name", () => {
    show([item("1", { sizeBytes: 2_500_000_000, qualityMet: true })]);
    const link = screen.getByRole("link", { name: /^Film 1/ });
    expect(link.getAttribute("title")).toMatch(/^Film 1 \(2001\) · Bluray 1080p · quality met · 2\.\d GB · .+ ago$|^Film 1 \(2001\) · Bluray 1080p · quality met · 2\.\d GB · just now$/);
    expect(link.getAttribute("aria-label")).toMatch(/^Film 1 \(2001\): in the library, (just now|.+ ago)$/);
  });

  it("leaves out of its tooltip and name what it does not have", () => {
    show([item("1", { quality: null })]);
    const link = screen.getByRole("link", { name: /^Film 1/ });
    expect(link.getAttribute("title")).toBe("Film 1 (2001) · just now");
    expect(link.getAttribute("aria-label")).toBe("Film 1 (2001): in the library, just now");
  });

  it("says a quality that can still be upgraded", () => {
    show([item("1", { qualityMet: false })]);
    expect(screen.getByRole("link", { name: /^Film 1/ }).getAttribute("title")).toBe("Film 1 (2001) · Bluray 1080p · can still be upgraded · just now");
  });

  it("says what an upgrade replaced in its tooltip and its name", () => {
    show([item("1", { upgraded: true, previousQuality: "WEB 720p" })]);
    const link = screen.getByRole("link", { name: /^Film 1/ });
    expect(link.getAttribute("title")).toBe("Film 1 (2001) · WEB 720p → Bluray 1080p · upgraded from WEB 720p · just now");
    expect(link.getAttribute("aria-label")).toBe("Film 1 (2001): in the library, upgraded from WEB 720p, just now");
  });

  it("names who asked for it in its tooltip", () => {
    show([item("1", { requestedBy: "Sam" })]);
    expect(screen.getByRole("link", { name: /^Film 1/ }).getAttribute("title")).toBe("Film 1 (2001) · Bluray 1080p · requested by Sam · just now");
  });

  it("marks its tile with its key, and the poster with the keys of what it holds", () => {
    show([item("1"), item("2")]);
    const tiles = Array.from(document.querySelectorAll(".lsh-poster"));
    expect(tiles.map((tile) => tile.getAttribute(TILE_KEY_ATTRIBUTE))).toEqual(["1", "2"]);
    const arts = Array.from(document.querySelectorAll<HTMLElement>(".lsh-art"));
    expect(arts.map((art) => art.dataset.arrivalPoster)).toEqual(["1", "2"]);
    expect(arts.map((art) => art.dataset.arrivalMembers)).toEqual(["1", "2"]);
  });

  it("lists the keys of every arrival a show's poster holds", () => {
    show([
      item("1", { entity: { kind: "series", id: "s1" }, release: "Show.S01E01.1080p-GRP" }),
      item("2", { entity: { kind: "series", id: "s1" }, release: "Show.S01E02.1080p-GRP" })
    ]);
    const art = document.querySelector<HTMLElement>(".lsh-art")!;
    expect(art.dataset.arrivalMembers).toBe("1|2");
    expect(art.dataset.arrivalPoster).toMatch(/^group:/);
  });

  it("rings a poster the moment any of its arrivals is new, and tags only a poster whose library is known", () => {
    render(<MemoryRouter><Shelf
      items={[item("1", { entity: { kind: "series", id: "s1" }, release: "Show.S01E01.1080p-GRP", libraryId: "kids", libraryName: "Kids" }), item("2", { entity: { kind: "series", id: "s1" }, release: "Show.S01E02.1080p-GRP", libraryId: "kids", libraryName: "Kids" }), item("3", { libraryId: "mystery", libraryName: null }), item("4")] as never[]}
      titles={NO_TITLES} newest={new Set(["2"])} heading="arrived today" library={section} /></MemoryRouter>);
    const tiles = Array.from(document.querySelectorAll<HTMLElement>(".lsh-poster"));
    expect(tiles.map((tile) => tile.getAttribute("data-new"))).toEqual(["", null, null]);
    expect(tiles.map((tile) => tile.getAttribute("data-tagged"))).toEqual(["", null, null]);
    expect(tiles.map((tile) => tile.querySelector(".lsh-tag")?.textContent ?? null)).toEqual(["Kids", null, null]);
  });

  it("tags a poster with the name it arrived with when the library's counts do not list it", () => {
    show([item("1", { libraryId: "elsewhere", libraryName: "Elsewhere" })], { library: section });
    expect(screen.getByRole("link", { name: "Elsewhere" })).toHaveAttribute("href", "/movies?library=elsewhere");
  });

  it("opens a show's library tag on the shows page", () => {
    show([item("1", { entity: { kind: "series", id: "s1" }, release: "Show.S01E01.1080p-GRP", libraryId: "kids", libraryName: "Kids" })], { library: section });
    expect(screen.getByRole("link", { name: "Kids" })).toHaveAttribute("href", "/tv?library=kids");
  });

  it("is a compact poster, its caption the title and the status, until the row has been measured", () => {
    box = { width: 0, height: 0 };
    show([item("1")]);
    expect(document.querySelector(".lsh-poster")).toHaveAttribute("data-caption", "compact");
    expect(document.querySelector(".lsh-posters")).not.toHaveAttribute("style");
  });

  it("sets the width of a poster on its row once the row is measured", () => {
    show([item("1")]);
    expect((document.querySelector(".lsh-posters") as HTMLElement).style.getPropertyValue("--aw")).toMatch(/^\d+px$/);
  });
});

describe("how many posters the row holds", () => {
  const many = (count: number) => Array.from({ length: count }, (_, index) => item(String(index + 1)));

  it("is as many as fit across, and more while a slot is open for one on its way", () => {
    show(many(12));
    const across = document.querySelectorAll(".lsh-poster").length;
    expect(across).toBeGreaterThan(0);
    expect(across).toBeLessThan(12);
    cleanup();
    vi.mocked(useOpenSlots).mockReturnValue(2);
    show(many(12));
    expect(document.querySelectorAll(".lsh-poster").length).toBe(across + 2);
    vi.mocked(useOpenSlots).mockRestore();
  });

  it("shows them all before the row has been measured", () => {
    box = { width: 0, height: 0 };
    show(many(12));
    expect(document.querySelectorAll(".lsh-poster")).toHaveLength(12);
  });

  it("says nothing has arrived, or nothing in the last seven days", () => {
    show([]);
    expect(screen.getByText("Nothing has arrived yet today.")).toBeInTheDocument();
    cleanup();
    show([], { latest: true });
    expect(screen.getByText("Nothing in the last seven days.")).toBeInTheDocument();
  });

  it("tells the row's motion which posters are in it and how wide they are, and the arrivals which are on the shelf", () => {
    vi.mocked(useSlideNeighbours).mockClear();
    vi.mocked(useTileArrivals).mockClear();
    show(many(12));
    const inRow = Array.from(document.querySelectorAll(".lsh-poster")).map((tile) => tile.getAttribute(TILE_KEY_ATTRIBUTE)).join("|");
    const [, order, width] = vi.mocked(useSlideNeighbours).mock.calls.at(-1)!;
    expect(order).toBe(inRow);
    expect(width).toBe(Number.parseInt((document.querySelector(".lsh-posters") as HTMLElement).style.getPropertyValue("--aw"), 10));
    const [, filed] = vi.mocked(useTileArrivals).mock.calls.at(-1)!;
    expect(filed).toBe(many(12).map((entry) => (entry as { key: string }).key).join("|"));
  });

  it("tells the row's motion no width before the row has been measured", () => {
    box = { width: 0, height: 0 };
    vi.mocked(useSlideNeighbours).mockClear();
    show(many(2));
    expect(vi.mocked(useSlideNeighbours).mock.calls.at(-1)![2]).toBeNull();
  });
});

describe("when what it is given changes", () => {
  it("shows the arrivals it is given now, and the libraries it is given now", () => {
    const view = render(<MemoryRouter><Shelf items={[item("1", { libraryId: "kids", libraryName: "Kids" })] as never[]} titles={NO_TITLES} newest={new Set()} heading="arrived today" library={section} /></MemoryRouter>);
    expect(screen.getByRole("link", { name: "Kids" })).toBeInTheDocument();
    const renamed = { ...(section as object), libraryCounts: [counts("kids", "Children")] } as never;
    view.rerender(<MemoryRouter><Shelf items={[item("1", { libraryId: "kids", libraryName: "Kids" }), item("2")] as never[]} titles={NO_TITLES} newest={new Set()} heading="arrived today" library={renamed} /></MemoryRouter>);
    expect(screen.getByRole("link", { name: "Children" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /^Film 2/ })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Children" })).toBeInTheDocument();
    expect(screen.queryByRole("radio", { name: "Kids" })).toBeNull();
  });

  it("offers library chips only while there are libraries, All first, and shows All when the chosen one is gone", () => {
    localStorage.setItem(SHELF_LIBRARY_KEY, "gone");
    show([item("1", { libraryId: "kids", libraryName: "Kids" })], { library: section });
    expect(screen.getAllByRole("radio").map((radio) => radio.textContent)).toEqual(["All", "Movies HD", "Kids"]);
    expect(screen.getByRole("radio", { name: "All" })).toBeChecked();
    expect(screen.getByRole("link", { name: /^Film 1/ })).toBeInTheDocument();
    cleanup();
    show([item("1")]);
    expect(screen.queryByRole("radio")).toBeNull();
  });
});

describe("the height the shelf needs, again", () => {
  const sizes = { clientHeight: 200, clientWidth: 900, offsetHeight: 260 };
  const measure = () => { for (const [name, value] of Object.entries(sizes)) vi.spyOn(HTMLElement.prototype, name as "clientHeight", "get").mockReturnValue(value); };

  it("is the card's chrome plus the height the posters can use at this width", () => {
    measure();
    const need = vi.fn();
    show([item("1")], { onHeightNeed: need });
    expect(need).toHaveBeenCalledWith(260 - 200 + shelfRowNeed(900));
  });

  it("is not told when nobody asked, or when the row has no height yet", () => {
    measure();
    show([item("1")]);
    cleanup();
    vi.restoreAllMocks();
    for (const [name, value] of Object.entries({ ...sizes, clientHeight: 0 })) vi.spyOn(HTMLElement.prototype, name as "clientHeight", "get").mockReturnValue(value);
    const need = vi.fn();
    show([item("1")], { onHeightNeed: need });
    expect(need).not.toHaveBeenCalled();
  });

  it("is told again when the row is measured again", () => {
    measure();
    const need = vi.fn();
    const view = show([item("1")], { onHeightNeed: need });
    expect(need).toHaveBeenCalledTimes(1);
    box = { width: 800, height: 300 };
    view.rerender(<MemoryRouter><Shelf items={[item("1")] as never[]} titles={NO_TITLES} newest={new Set()} heading="arrived today" onHeightNeed={need} /></MemoryRouter>);
    expect(need).toHaveBeenCalledTimes(2);
  });
});
