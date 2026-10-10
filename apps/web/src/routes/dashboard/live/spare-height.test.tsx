import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { readFileSync } from "node:fs";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { episodeSpanOf } from "./arrival-groups";
import { BAND_MIN_PX, BAND_TALL_MAX_PX, gridRows, libraryPosters, lowNeedOfGrid, shelfRowNeed } from "./layout";
import { lineWords } from "./caption-fit";
import { posterDetail, posterStatus } from "./poster-caption";
import { Shelf } from "./shelf";
import { NO_TITLES } from "./titles";

// The shelf's box, which each test sets: the poster size and caption tier come from it.
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

afterEach(() => {
  cleanup();
  charPx = null;
});

describe("gridRows told what the bottom row can use (lowNeed), the same rule as Weir's", () => {
  it("keeps the old rule without lowNeed", () => {
    expect(gridRows(1080, 1080)).toEqual(gridRows(1080, 1080, {}));
    expect(gridRows(1080, 1080)).toMatchObject({ band: 240, journey: 468, low: 348 });
  });
  it("never lets the band under 144px (Weir's): at 130px the band tiles lose their row", () => {
    expect(BAND_MIN_PX).toBe(144);
    expect(gridRows(0, 300).band).toBe(144);
  });
  it("shows Weir's rows on a short window: the band at 144px, the journey at its least and the bottom row the rest", () => {
    // 1366x768 in Weir: the grid is 630px high (Weir's header wraps there), so 144 / 324 / 138.
    expect(gridRows(0, 630, { lowNeed: 195 }).template).toBe("144px 324px 138px");
    // 1538x784: 676px, so the journey has 328px and the bottom row 180px, whatever the bottom row says it needs.
    expect(gridRows(0, 676, { lowNeed: 180 }).template).toBe("144px 328px 180px");
    expect(gridRows(0, 676, { lowNeed: 237 }).template).toBe("144px 328px 180px");
    expect(gridRows(0, 676).template).toBe("144px 328px 180px");
  });
  it("gives spare height first to the journey (up to the 130px step), then to the band (to 360px), and the bottom row keeps the rest", () => {
    // 1920x1080 as Weir measured it: the bottom row keeps its need (92 chrome + 154.5 art + 64 caption + 12 = 323).
    expect(gridRows(0, 964, { lowNeed: 323 })).toMatchObject({ band: 193, journey: 424, low: 323 });
    // A window tall enough for the journey at its tallest: the rest goes to the band.
    expect(gridRows(0, 1183, { lowNeed: 332 })).toMatchObject({ band: 321, journey: 506, low: 332 });
    // So tall that the band is full as well: the bottom row keeps what is left.
    expect(BAND_TALL_MAX_PX).toBe(360);
    expect(gridRows(0, 1323, { lowNeed: 433 })).toEqual({ band: 360, journey: 506, low: 433, template: "360px 506px 433px" });
    expect(gridRows(0, 1523, { lowNeed: 433 })).toMatchObject({ band: 360, journey: 506, low: 633 });
  });
  it("keeps at least 180px for the bottom row however little it says it needs", () => {
    expect(gridRows(0, 964, { lowNeed: 20 })).toEqual(gridRows(0, 964, { lowNeed: 180 }));
  });
  it("gives nothing away from a bottom row at or under its need", () => {
    expect(gridRows(0, 700, { lowNeed: 500 })).toEqual(gridRows(0, 700));
  });
});

describe("what the shelf needs (shelfRowNeed) and its caption tiers (libraryPosters)", () => {
  it("is the art of the widest poster the five-across rule allows, its caption, and the 12px of padding", () => {
    // 574px: widest = floor((574 - 10 + 12) / 5) - 12 = 103, art = ceil(154.5) = 155, full caption 72 (Weir's four 15px lines, 2px apart, and the 6px above).
    expect(shelfRowNeed(574)).toBe(12 + 155 + 72);
    // 2000px wide: capped at 170, art 255.
    expect(shelfRowNeed(2000)).toBe(12 + 255 + 72);
    // 430px: posters 74px wide, art 111.
    expect(shelfRowNeed(430)).toBe(12 + 111 + 72);
    // Under 64px wide there is no full caption, only the tiny one (the 21px status line): 300px gives 48px posters and 72px art.
    expect(shelfRowNeed(300)).toBe(12 + 72 + 21);
    // 330px gives 54px posters, 81px art. At 400px the posters are 68px wide, so full again.
    expect(shelfRowNeed(330)).toBe(12 + 81 + 21);
    // 24px is the narrowest poster, 36px the least art.
    expect(shelfRowNeed(100)).toBe(12 + 36 + 21);
  });
  it("is the same for System, which works it out from the grid's width alone", () => {
    // 1920x1080: the grid is 1601px wide, the shelf's row 574px, and the card around it 60px (the chips are in its header).
    expect(lowNeedOfGrid(1601)).toBe(60 + shelfRowNeed(574));
  });
  it("chooses the full caption only for a poster wide enough with a row tall enough, the compact one from 134px, the tiny one from 57px, and none under it", () => {
    expect(libraryPosters(239, 574)).toEqual({ captions: true, caption: "full", width: 103 });
    // One pixel short of the art and the full caption: compact.
    expect(libraryPosters(238, 574).caption).toBe("compact");
    // The row's own 12px of padding: 134px inside it is 146px.
    expect(libraryPosters(146, 574).caption).toBe("compact");
    expect(libraryPosters(145, 574)).toEqual({ captions: true, caption: "tiny", width: 74 });
    expect(libraryPosters(69, 574).caption).toBe("tiny");
    expect(libraryPosters(68, 574)).toMatchObject({ captions: false, caption: "none" });
    // Weir's 1366x768 row: 138px of grid less the 60px of card is 78px, 66 inside its padding, 45px of art under a 21px caption: 30px posters.
    expect(libraryPosters(78, 400)).toEqual({ captions: true, caption: "tiny", width: 30 });
    // A narrow row's posters are under 64px: never the full caption, however tall.
    expect(libraryPosters(400, 300).caption).toBe("compact");
    // Not measured across yet: no full caption.
    expect(libraryPosters(400).caption).toBe("compact");
  });
});

describe("the words under a poster", () => {
  const base = { met: null, quality: "Bluray 1080p", upgrades: 0, items: [{}], previousQuality: null, episodes: null, episodeSpan: null, sizeBytes: 0 } as never as Parameters<typeof posterStatus>[0] & Parameters<typeof posterDetail>[0];
  const group = (extra: object) => ({ ...base, ...extra }) as typeof base;
  it("says the status in the Movies page's legend: gold met, green can be upgraded, grey when it is not known", () => {
    expect(posterStatus(group({ met: true }))).toMatchObject({ tone: "met", words: "Quality met · Bluray 1080p" });
    expect(posterStatus(group({ met: false, quality: "WEB 720p" }))).toMatchObject({ tone: "upgradable", words: "Can be upgraded · WEB 720p" });
    expect(posterStatus(group({ met: null }))).toMatchObject({ tone: "unknown", words: "Imported" });
  });
  it("has short words for the tiny caption, fullest first, ending in nothing so the dot can stand alone", () => {
    expect(posterStatus(group({ met: true })).short).toEqual(["Quality met", "Met", ""]);
    expect(posterStatus(group({ met: false })).short).toEqual(["Can be upgraded", "Upgradable", ""]);
    expect(posterStatus(group({ met: null })).short).toEqual(["Imported", ""]);
    expect(posterStatus(group({ met: true, upgrades: 1 })).short).toEqual(["Upgraded", ""]);
    expect(posterStatus(group({ met: true, upgrades: 2, items: [{}, {}] })).short).toEqual(["2 upgraded", ""]);
    const words = posterStatus(group({ met: true })).short;
    // 64px wide, less the 11px the dot takes: "Quality met" (66px at 6px a character) does not fit, "Met" does; at 25px only the dot is left.
    const measure = (text: string) => text.length * 6;
    expect(lineWords(words, 64 - 11, measure)).toBe("Met");
    expect(lineWords(words, 120, measure)).toBe("Quality met");
    expect(lineWords(words, 25 - 11, measure)).toBe("");
  });
  it("keeps the colour of an upgrade that landed and says Upgraded", () => {
    expect(posterStatus(group({ met: true, upgrades: 1 }))).toMatchObject({ tone: "met", words: "Upgraded · Bluray 1080p" });
    expect(posterStatus(group({ met: false, upgrades: 1 }))).toMatchObject({ tone: "upgradable", words: "Upgraded · Bluray 1080p" });
    expect(posterStatus(group({ met: true, upgrades: 2, items: [{}, {}, {}] })).words).toBe("2 upgraded · Bluray 1080p");
  });
  it("has the size, or the episodes, or what an upgrade replaced, on the detail line", () => {
    expect(posterDetail(group({ sizeBytes: 2.5 * 1024 ** 3 }))).toBe("2.5 GB");
    expect(posterDetail(group({ upgrades: 1, previousQuality: "WEB 720p", sizeBytes: 2.5 * 1024 ** 3 }))).toBe("from WEB 720p");
    expect(posterDetail(group({ episodes: "4 episodes", episodeSpan: "S01E05–E08 · 4 episodes" }))).toBe("S01E05–E08 · 4 episodes");
    expect(posterDetail(group({ episodes: "S01E04", sizeBytes: 1e9 }))).toBe("S01E04");
    expect(posterDetail(group({}))).toBe("");
  });
  it("works out a run of one season's episodes, and says nothing where the numbers are not known or cross seasons", () => {
    expect(episodeSpanOf(["S01E05", "S01E08", "S01E06", "S01E07"])).toBe("S01E05–E08 · 4 episodes");
    expect(episodeSpanOf(["S02E01", "S02E01"])).toBe("S02E01 · 2 episodes");
    expect(episodeSpanOf(["S01E05", null])).toBeNull();
    expect(episodeSpanOf(["S01E05", "S02E01"])).toBeNull();
    expect(episodeSpanOf(["S01E05"])).toBeNull();
  });
});

describe("the shelf's captions", () => {
  // The shelf lists the newest arrival first, so each film arrived a minute before the one before it: the order is fixed, not left to
  // whether the clock ticked between two calls to new Date().
  const arrived = Date.now();
  const item = (key: string, extra: object = {}) => ({ key, entity: { kind: "movie", id: key }, fallbackTitle: `Film ${key} (2001)`, release: null, quality: "Bluray 1080p", previousQuality: null, upgraded: false, sizeBytes: 2.5 * 1024 ** 3, at: new Date(arrived - Number(key) * 60_000).toISOString(), requestedBy: null, to: `/movies/${key}`, qualityMet: true, libraryId: "hd", libraryName: "Movies HD", ...extra }) as never;
  const show = (extra: object = {}) => render(<MemoryRouter><Shelf items={[item("1"), item("2", { upgraded: true, previousQuality: "WEB 720p", qualityMet: false })]} titles={NO_TITLES} newest={new Set()} heading="2 arrived today" {...extra} /></MemoryRouter>);

  it("is four level slots in the full tier: the title, the status, the detail and when, each present on every poster", () => {
    box = { width: 900, height: 340 };
    show();
    const caps = Array.from(document.querySelectorAll(".lsh-cap--full"));
    expect(caps).toHaveLength(2);
    for (const cap of caps) {
      expect(Array.from(cap.children).map((child) => child.className)).toEqual(["lsh-title", "lsh-status", "lsh-detail", "lsh-when"]);
    }
    expect(caps[0].querySelector(".lsh-detail")).toHaveTextContent("2.5 GB");
    expect(caps[1].querySelector(".lsh-detail")).toHaveTextContent("from WEB 720p");
    expect(caps[1].querySelector(".lsh-when")).toHaveTextContent(/now|ago/);
    expect(document.querySelector(".lsh-up, .lsh-mark")).toBeNull();
  });
  it("is the title and the status in the compact tier, the status alone in the tiny tier, and nothing in the none tier", () => {
    box = { width: 900, height: 200 };
    show();
    expect(document.querySelector(".lsh-cap--full")).toBeNull();
    const cap = document.querySelector(".lsh-cap")!;
    expect(Array.from(cap.children).map((child) => child.className)).toEqual(["lsh-title", "lsh-status"]);
    cleanup();
    box = { width: 900, height: 140 };
    show();
    const tiny = document.querySelector(".lsh-cap")!;
    expect(tiny.getAttribute("data-tier")).toBe("tiny");
    expect(Array.from(tiny.children).map((child) => child.className)).toEqual(["lsh-status"]);
    expect(document.querySelector(".lsh-poster")?.getAttribute("data-caption")).toBe("tiny");
    cleanup();
    box = { width: 900, height: 60 };
    show();
    expect(document.querySelector(".lsh-cap")).toBeNull();
  });
  it("says the shortest of its words that fits under the poster in the tiny tier, and the dot alone where none does", () => {
    // 900px wide, 140px high: 128px of row, 107px of art under the 21px line, posters 71px wide; 71 - 11 = 60px for the words.
    box = { width: 900, height: 140 };
    charPx = 6;
    show();
    // Poster 1 is quality met: "Quality met" is 66px (too wide), "Met" is 18px. Poster 2 had an upgrade land: "Upgraded" (48px) fits.
    const words = () => Array.from(document.querySelectorAll(".lsh-cap .lsh-status > span")).map((node) => node.textContent);
    expect(words()).toEqual(["Met", "Upgraded"]);
    cleanup();
    charPx = 25;
    show();
    // At 25px a character nothing but the dot fits: the words are empty (and hidden by the stylesheet), the line is the dot alone.
    expect(words()).toEqual(["", ""]);
    expect(document.querySelectorAll(".lsh-cap .lsh-dot")).toHaveLength(2);
    // The full words stay in the poster's tooltip and name.
    expect(document.querySelector(".lsh-poster > a")?.getAttribute("title")).toMatch(/quality met/);
    cleanup();
    charPx = null;
    show();
    // Not measurable (no canvas): the fullest short words are taken to fit.
    expect(words()).toEqual(["Quality met", "Upgraded"]);
  });
  it("has the library chips in the card's header in place of the count, which stays as the card's description, and picks one on click", () => {
    box = { width: 900, height: 340 };
    const library = { libraryCounts: [{ libraryId: "hd", libraryName: "Movies HD" }, { libraryId: "kids", libraryName: "Kids" }] } as never;
    show({ library, detail: "2 titles · 1 upgraded" });
    const section = screen.getByRole("region", { name: "Your library" });
    const header = section.querySelector("header")!;
    expect(within(header).getByRole("radiogroup", { name: "Libraries" })).toBeInTheDocument();
    expect(within(header).getAllByRole("radio").map((chip) => chip.textContent)).toEqual(["All", "Movies HD", "Kids"]);
    // The count is not drawn in the header; a screen reader still hears it as the card's description.
    expect(header).not.toHaveTextContent("arrived today");
    const note = document.getElementById(section.getAttribute("aria-describedby")!)!;
    expect(note).toHaveClass("sr-only");
    expect(note).toHaveTextContent("2 arrived today: 2 titles · 1 upgraded");
    fireEvent.click(within(header).getByRole("radio", { name: "Kids" }));
    expect(within(header).getByRole("radio", { name: "Kids" })).toHaveAttribute("aria-checked", "true");
  });
  it("keeps the count in the header, and no description, when there are no libraries to choose between", () => {
    box = { width: 900, height: 340 };
    show();
    const section = screen.getByRole("region", { name: "Your library" });
    expect(section.querySelector("header")).toHaveTextContent("2 arrived today");
    expect(section.hasAttribute("aria-describedby")).toBe(false);
  });
  it("tells the page what its bottom row can use, which does not depend on its height", () => {
    box = { width: 900, height: 340 };
    const sizes = { clientHeight: 300, clientWidth: 574, offsetHeight: 392 };
    const spies = (Object.keys(sizes) as (keyof typeof sizes)[]).map((key) => vi.spyOn(HTMLElement.prototype, key, "get").mockImplementation(() => sizes[key]));
    const told = vi.fn();
    show({ onHeightNeed: told });
    expect(told).toHaveBeenCalledWith(392 - 300 + shelfRowNeed(574));
    cleanup();
    told.mockClear();
    box = { width: 900, height: 420 };
    show({ onHeightNeed: told });
    expect(told).toHaveBeenCalledWith(392 - 300 + shelfRowNeed(574));
    for (const spy of spies) spy.mockRestore();
  });
});

describe("a station's label never runs into its icon", () => {
  const css = readFileSync("src/routes/dashboard/live/journey.css", "utf8").replace(/\r\n/g, "\n");
  const rule = (selector: string) => css.split("\n").find((line) => line.startsWith(`${selector} {`)) ?? "";
  it("lets the text shrink and truncate with an ellipsis, the stage's name kept before the count", () => {
    const text = rule(".lj-text");
    expect(text).toMatch(/min-width: 0/);
    expect(text).toMatch(/white-space: nowrap/);
    expect(text).toMatch(/overflow: hidden/);
    // The name stays whole while the count gives way first.
    expect(rule(".lj-nm")).toMatch(/flex: 0 0 auto/);
    expect(rule(".lj-ct")).toMatch(/flex: 0 1 auto/);
    for (const selector of [".lj-nm", ".lj-ct"]) {
      expect(rule(selector)).toMatch(/min-width: 0/);
      expect(rule(selector)).toMatch(/overflow: hidden/);
      expect(rule(selector)).toMatch(/text-overflow: ellipsis/);
    }
    expect(rule(".lj-dot")).toMatch(/flex: none/);
  });
});
