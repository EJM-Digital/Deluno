import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { arrivedDetail, arrivedHeading, arrivedWhen, libraryHue, LIBRARY_HUES, SHELF_LIBRARY_KEY, Shelf } from "./shelf";
import { TILE_KEY_ATTRIBUTE, useSlideNeighbours, useTileArrivals } from "./shelf-motion";
import { useOpenSlots } from "./shelf-slots";
import { NO_TITLES } from "./titles";

// The shelf's box, which a test sets: jsdom measures nothing.
let box = { width: 900, height: 330 };
vi.mock("./hooks", async (importOriginal) => {
  const { createRef } = await import("react");
  return { ...(await importOriginal<typeof import("./hooks")>()), useElementSize: () => [createRef(), box] };
});

// The canvas the status line is measured on: a test sets the width of one character, or leaves it unmeasured (null).
let charPx: number | null = null;
vi.mock("./caption-fit", async (importOriginal) => {
  const original = await importOriginal<typeof import("./caption-fit")>();
  return { ...original, statusFont: () => "600 12px sans-serif", canvasMeasure: () => (charPx === null ? null : (text: string) => text.length * charPx!) };
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
afterEach(() => { cleanup(); vi.restoreAllMocks(); box = { width: 900, height: 330 }; charPx = null; });

const NOW = new Date("2026-10-31T12:00:00Z").getTime();
// One time for every item: read per item, the clock could tick between two and reorder a newest-first shelf (CI, 5 Nov).
const ARRIVED_AT = new Date().toISOString();
const item = (key: string, extra: object = {}) => ({ key, entity: { kind: "movie", id: key }, fallbackTitle: `Film ${key} (2001)`, release: null, quality: "Bluray 1080p", previousQuality: null, upgraded: false, sizeBytes: null, at: ARRIVED_AT, requestedBy: null, to: `/movies/${key}`, qualityMet: null, libraryId: null, libraryName: null, ...extra }) as never;
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

  it("says nothing has arrived only when the library has never had an arrival", () => {
    show([]);
    expect(screen.getByText("Nothing has arrived yet.")).toBeInTheDocument();
  });

  it("never shows more than twelve titles, however wide the row (#1531)", () => {
    box = { width: 4000, height: 330 };
    show(many(20));
    expect(document.querySelectorAll(".lsh-poster")).toHaveLength(12);
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

describe("the header and the colours", () => {
  it("counts what arrived today, with thousands grouped", () => {
    expect(arrivedHeading(12)).toBe("12 arrived today");
    expect(arrivedHeading(1234)).toBe("1,234 arrived today");
  });

  it("adds up the titles, the upgrades and the size when there are some", () => {
    expect(arrivedDetail(1, 0, 0)).toBe("1 title");
    expect(arrivedDetail(12, 0, 0)).toBe("12 titles");
    expect(arrivedDetail(12, 4, 0)).toBe("12 titles · 4 upgraded");
    expect(arrivedDetail(12, 0, 54 * 1024 ** 3)).toBe("12 titles · 54 GB added");
    expect(arrivedDetail(3, 1200, 2.5 * 1024 ** 3)).toBe("3 titles · 1,200 upgraded · 2.5 GB added");
  });

  it("gives each library a colour by its place, going round again after six, and the first for one it cannot place", () => {
    expect(LIBRARY_HUES).toEqual([205, 265, 320, 188, 232, 290]);
    expect([0, 1, 2, 3, 4, 5, 6, 7].map(libraryHue)).toEqual([205, 265, 320, 188, 232, 290, 205, 265]);
    expect(libraryHue(-1)).toBe(205);
    expect(SHELF_LIBRARY_KEY).toBe("deluno.live.shelfLibrary");
  });

  it("describes the card by its heading and what it adds up to, for a screen reader, when there are chips", () => {
    show([item("1")], { library: section, heading: "2 arrived today", detail: "2 titles · 1 upgraded" });
    const card = document.querySelector("section")!;
    expect(document.getElementById(card.getAttribute("aria-describedby")!)).toHaveTextContent("2 arrived today: 2 titles · 1 upgraded");
    cleanup();
    show([item("1")], { library: section, heading: "2 arrived today" });
    const bare = document.querySelector("section")!;
    expect(document.getElementById(bare.getAttribute("aria-describedby")!)).toHaveTextContent("2 arrived today");
    cleanup();
    show([item("1")], { heading: "2 arrived today", detail: "2 titles" });
    expect(document.querySelector("section")).not.toHaveAttribute("aria-describedby");
  });

  it("tags a poster with its library's name, its colour and an Open link", () => {
    show([item("1", { libraryId: "kids", libraryName: "Kids" }), item("2", { libraryId: "hd", libraryName: "Movies HD" })], { library: section });
    const kids = screen.getByRole("link", { name: "Kids" });
    expect(kids).toHaveAttribute("title", "Open Kids in the library");
    expect(kids.style.getPropertyValue("--tile-hue")).toBe(String(LIBRARY_HUES[1]));
    expect(screen.getByRole("link", { name: "Movies HD" }).style.getPropertyValue("--tile-hue")).toBe(String(LIBRARY_HUES[0]));
  });

  it("gives a tag for a library the counts do not list the first colour", () => {
    show([item("1", { libraryId: "elsewhere", libraryName: "Elsewhere" })], { library: section });
    expect(screen.getByRole("link", { name: "Elsewhere" }).style.getPropertyValue("--tile-hue")).toBe(String(LIBRARY_HUES[0]));
  });
});

describe("the caption tiers", () => {
  const two = () => show([item("1", { qualityMet: true, sizeBytes: 2.5 * 1024 ** 3 }), item("2", { upgraded: true, previousQuality: "WEB 720p", qualityMet: false })]);
  const words = () => Array.from(document.querySelectorAll(".lsh-cap .lsh-status > span")).map((node) => node.textContent);

  it("is four level slots in the full tier", () => {
    box = { width: 900, height: 340 };
    two();
    const caps = Array.from(document.querySelectorAll(".lsh-cap--full"));
    expect(caps).toHaveLength(2);
    for (const cap of caps) expect(Array.from(cap.children).map((child) => child.className)).toEqual(["lsh-title", "lsh-status", "lsh-detail", "lsh-when"]);
    expect(caps[0].getAttribute("data-tier")).toBe("full");
    expect(caps[0].querySelector(".lsh-detail")).toHaveTextContent("2.5 GB");
    expect(caps[1].querySelector(".lsh-detail")).toHaveTextContent("from WEB 720p");
    expect(caps[0].querySelector(".lsh-when")).toHaveTextContent(/now|ago/);
    expect(caps[0].querySelector(".lsh-status")).toHaveAttribute("data-tone", "met");
    expect(caps[0].querySelector(".lsh-status")).toHaveTextContent("Quality met · Bluray 1080p");
    expect(caps[1].querySelector(".lsh-status")).toHaveTextContent("Upgraded · Bluray 1080p");
    expect(caps[1].querySelector(".lsh-status")).toHaveAttribute("data-tone", "upgradable");
  });

  it("is the title and the status in the compact tier", () => {
    box = { width: 900, height: 200 };
    two();
    expect(document.querySelector(".lsh-cap--full")).toBeNull();
    const cap = document.querySelector(".lsh-cap")!;
    expect(cap.getAttribute("data-tier")).toBe("compact");
    expect(Array.from(cap.children).map((child) => child.className)).toEqual(["lsh-title", "lsh-status"]);
    expect(cap.className).toBe("lsh-cap");
    expect(cap.querySelector(".lsh-status")).toHaveTextContent("Quality met · Bluray 1080p");
    expect(document.querySelector(".lsh-poster")).toHaveAttribute("data-caption", "compact");
  });

  it("is the status alone in the tiny tier, and nothing in the none tier", () => {
    box = { width: 900, height: 140 };
    two();
    const tiny = document.querySelector(".lsh-cap")!;
    expect(tiny.getAttribute("data-tier")).toBe("tiny");
    expect(Array.from(tiny.children).map((child) => child.className)).toEqual(["lsh-status"]);
    cleanup();
    box = { width: 900, height: 60 };
    two();
    expect(document.querySelector(".lsh-cap")).toBeNull();
    expect(document.querySelector(".lsh-poster")).toHaveAttribute("data-caption", "none");
  });

  it("says the shortest of its words that fits under the poster in the tiny tier, and the dot alone where none does", () => {
    box = { width: 900, height: 140 };
    charPx = 6;
    two();
    expect(words()).toEqual(["Met", "Upgraded"]);
    cleanup();
    charPx = 25;
    two();
    expect(words()).toEqual(["", ""]);
    expect(document.querySelectorAll(".lsh-cap .lsh-dot")).toHaveLength(2);
    cleanup();
    charPx = null;
    two();
    expect(words()).toEqual(["Quality met", "Upgraded"]);
  });

  it("measures the status line in its own font again when the fonts arrive", async () => {
    box = { width: 900, height: 140 };
    charPx = 6;
    const ready = Promise.resolve();
    Object.defineProperty(document, "fonts", { configurable: true, value: { ready } });
    try {
      two();
      await act(async () => { await ready; });
      // reason: the re-measure runs on the next frame after the fonts promise; a busy test machine took over the 1 s default
      await waitFor(() => expect(words()).toEqual(["Met", "Upgraded"]), { timeout: 5_000 });
    } finally {
      // Taken away however the test ends, so no later test sees these fonts.
      delete (document as unknown as { fonts?: unknown }).fonts;
    }
  });

  it("draws small posters at under 64px wide and full ones from there", () => {
    box = { width: 340, height: 200 };
    two();
    expect(document.querySelector(".lsh-poster")).toHaveAttribute("data-size", "small");
    cleanup();
    box = { width: 900, height: 330 };
    two();
    expect(document.querySelector(".lsh-poster")).toHaveAttribute("data-size", "full");
  });
});
