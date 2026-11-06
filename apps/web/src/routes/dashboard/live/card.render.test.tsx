import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Count, JourneyToken, PosterSlot, PosterTile, statusOf } from "./card";
import { IMPORTING_NOW, WAITING_FOR_PROCESSOR, type JourneyCard } from "./journey";
import { NO_TITLES } from "./titles";

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const card = (overrides: Partial<JourneyCard> = {}): JourneyCard => ({
  key: "k1", stage: "downloading", title: "Heat (1995)", state: { tone: "info", label: "Downloading" }, to: "/queue", ...overrides
});
const IMPORTING = "importing" as const;
const IMPORTED = "imported" as const;
const searching = { stage: "searching" as const, state: { tone: "info" as const, label: "Searching" } };

describe("what a card says about itself", () => {
  it("says what a search is doing: for Seerr, with indexers counted, or plainly", () => {
    expect(statusOf(card({ ...searching, startedBy: "seerr" }), 5)).toEqual({ text: "Asked for in Seerr · checking", tone: "info", pulse: true });
    expect(statusOf(card(searching), 5).text).toBe("Checking 5 indexers");
    expect(statusOf(card(searching), 1).text).toBe("Checking 1 indexer");
    expect(statusOf(card(searching), 0).text).toBe("Looking for it");
  });

  it("puts a show's season or episode in front of a search's words, and leaves the plain labels out", () => {
    expect(statusOf(card({ stage: "searching", state: { tone: "info", label: "Season 2" } }), 3).text).toBe("Season 2 · checking 3 indexers");
    expect(statusOf(card({ stage: "searching", state: { tone: "info", label: "S02E05" } }), 0).text).toBe("S02E05 · looking for it");
    expect(statusOf(card({ stage: "searching", state: { tone: "info", label: "The whole show" } }), 0).text).toBe("The whole show · looking for it");
  });

  it("keeps a search's own words when the label only mentions a season or an episode", () => {
    expect(statusOf(card({ stage: "searching", state: { tone: "info", label: "Looking for Season 2" } }), 3).text).toBe("Checking 3 indexers");
    expect(statusOf(card({ stage: "searching", state: { tone: "info", label: "Not S02E05" } }), 3).text).toBe("Checking 3 indexers");
    expect(statusOf(card({ stage: "searching", state: { tone: "info", label: "Not The whole show" } }), 3).text).toBe("Checking 3 indexers");
  });

  it("is full and green once the import has finished", () => {
    expect(statusOf(card({ stage: IMPORTING, landed: true }), 0)).toEqual({ text: "✓ Imported · in your library", tone: "ok", pulse: false, lead: "✓ Imported" });
  });

  it("says a stuck download is stuck, with its percentage and what happens next when it has moved", () => {
    expect(statusOf(card({ stuck: true, progress: 42.7, stuckNext: "replacing it soon" }), 0)).toEqual({ text: "Stuck at 42% · replacing it soon", tone: "warn", pulse: true });
    expect(statusOf(card({ stuck: true, progress: 0 }), 0).text).toBe("Stuck · not moving");
    expect(statusOf(card({ stuck: true }), 0).text).toBe("Stuck · not moving");
  });

  it("says what was grabbed: its quality and indexer, or the plain label when it has neither", () => {
    const grabbed = { stage: "grabbed" as const, state: { tone: "ok" as const, label: "Sent to your client" } };
    expect(statusOf(card({ ...grabbed, quality: "1080p", indexer: "NZBgeek" }), 0)).toEqual({ text: "1080p · from NZBgeek", tone: "ok", pulse: false });
    expect(statusOf(card({ ...grabbed, indexer: "NZBgeek" }), 0).text).toBe("from NZBgeek");
    expect(statusOf(card({ ...grabbed, quality: "2160p" }), 0).text).toBe("2160p");
    expect(statusOf(card(grabbed), 0).text).toBe("Sent to your client");
  });

  it("says a download's percentage, speed and client, bold at the front", () => {
    expect(statusOf(card({ progress: 63.9, speedMbps: 12.4, client: "qBittorrent" }), 0)).toEqual({ text: "63% · 12 MB/s · qBittorrent", tone: "info", pulse: false, lead: "63%" });
    expect(statusOf(card({ progress: 5, speedMbps: 0 }), 0).text).toBe("5%");
    expect(statusOf(card({ progress: 5, speedMbps: 2.5 }), 0).text).toBe("5% · 2.5 MB/s");
  });

  it("uses the client's own words when a download is not simply downloading", () => {
    expect(statusOf(card({ state: { tone: "warn", label: "Paused" } }), 0)).toEqual({ text: "Paused", tone: "warn", pulse: false });
  });

  it("says where the processor is: failed, waiting to be handed over, or working", () => {
    const processor = { stage: "processor" as const, processor: "Weir" };
    expect(statusOf(card({ ...processor, state: { tone: "bad", label: "Processor failed" } }), 0)).toEqual({ text: "Processor failed", tone: "bad", pulse: false });
    expect(statusOf(card({ ...processor, state: { tone: "info", label: WAITING_FOR_PROCESSOR } }), 0).text).toBe("Waiting to hand over to Weir");
    expect(statusOf(card({ ...processor, state: { tone: "info", label: WAITING_FOR_PROCESSOR } }), 0)).toEqual({ text: "Waiting to hand over to Weir", tone: "info", pulse: false });
    expect(statusOf(card({ stage: "processor", state: { tone: "info", label: WAITING_FOR_PROCESSOR } }), 0).text).toBe("Waiting to hand over to the processor");
    expect(statusOf(card({ ...processor, state: { tone: "info", label: "Cleaning" } }), 0)).toEqual({ text: "Weir is working on it", tone: "info", pulse: true });
    expect(statusOf(card({ stage: "processor", state: { tone: "info", label: "Cleaning" } }), 0).text).toBe("The processor is working on it");
  });

  it("pulses an import that is under way and not one that has failed", () => {
    expect(statusOf(card({ stage: IMPORTING, state: { tone: "info", label: IMPORTING_NOW } }), 0).pulse).toBe(true);
    expect(statusOf(card({ stage: IMPORTING, state: { tone: "bad", label: "Import failed" } }), 0)).toEqual({ text: "Import failed", tone: "bad", pulse: false });
  });

  it("uses the state's own words for a title that is already in the library, and never pulses it", () => {
    expect(statusOf(card({ stage: IMPORTED, state: { tone: "ok", label: "In the library" } }), 0)).toEqual({ text: "In the library", tone: "ok", pulse: false });
    expect(statusOf(card({ stage: IMPORTED, state: { tone: "info", label: "Just landed" } }), 0).pulse).toBe(false);
  });
});

const token = (overrides: Partial<JourneyCard> = {}, extra: Partial<Parameters<typeof JourneyToken>[0]> = {}) =>
  render(<MemoryRouter><JourneyToken card={card(overrides)} titles={NO_TITLES} arrivals={new Set()} indexers={0} {...extra} /></MemoryRouter>);

describe("a card on the journey", () => {
  it("links to its title's page and names itself by title and status", () => {
    token({ progress: 40, speedMbps: 3, client: "qBittorrent", indexer: "NZBgeek", to: "/movies/1" });
    const link = screen.getByRole("link", { name: "Heat (1995): 40% · 3.0 MB/s · qBittorrent" });
    expect(link).toHaveAttribute("href", "/movies/1");
    expect(link).toHaveAttribute("title", "Heat (1995) · NZBgeek → qBittorrent");
    expect(link).toHaveAttribute("data-glide-stage", "downloading");
    expect(link.className).toContain("lj-token lj-fresh");
    expect(link.className).not.toMatch(/lj-done|lj-bad|lj-warn|lj-hidden|lj-arrive/);
    expect(link).not.toHaveAttribute("tabindex");
  });

  it("draws a moving bar for a download that has speed, and a still one when it has none or is stuck", () => {
    const bar = () => document.querySelector(".lj-pb") as HTMLElement;
    token({ progress: 40, speedMbps: 3 });
    expect(bar().className).toContain("lj-moving");
    expect(bar().firstElementChild).toHaveStyle({ width: "40%" });
    cleanup();
    token({ progress: 40, speedMbps: 0 });
    expect(bar().className).not.toContain("lj-moving");
    cleanup();
    token({ progress: 40, speedMbps: 3, stuck: true });
    expect(bar().className).not.toContain("lj-moving");
  });

  it("shows an import under way as a waiting bar, a finished one as full and green, and a waiting card with none", () => {
    token({ stage: IMPORTING, state: { tone: "info", label: IMPORTING_NOW } });
    expect(document.querySelector(".lj-pb")?.className).toContain("lj-wait");
    expect(document.querySelector(".lj-pb")?.className).toContain("lj-moving");
    expect(document.querySelector(".lj-pb")?.firstElementChild).toHaveStyle({ width: "35%" });
    cleanup();
    token({ stage: IMPORTING, landed: true, state: { tone: "ok", label: "Imported" } });
    expect(document.querySelector(".lj-pb")?.firstElementChild).toHaveStyle({ width: "100%" });
    expect(document.querySelector(".lj-pb")?.className).not.toMatch(/lj-wait|lj-moving/);
    expect(screen.getByRole("link").className).toContain("lj-done");
    expect(screen.getByRole("link").style.getPropertyValue("--lj-st")).toBe("var(--lj-success)");
    cleanup();
    token({ stage: IMPORTING, state: { tone: "info", label: "Waiting" } });
    expect(document.querySelector(".lj-pb")).toBeNull();
  });

  it("draws no bar on a station that only waits, whatever the card says of itself", () => {
    token({ stage: "grabbed", landed: true, state: { tone: "ok", label: "Sent" } });
    expect(document.querySelector(".lj-pb")).toBeNull();
    cleanup();
    token({ stage: "grabbed", state: { tone: "ok", label: IMPORTING_NOW } });
    expect(document.querySelector(".lj-pb")).toBeNull();
  });

  it("edges a failed card in red and a stuck or warned one in amber", () => {
    token({ stage: IMPORTING, state: { tone: "bad", label: "Import failed" } });
    expect(screen.getByRole("link").className).toContain("lj-bad");
    expect(screen.getByRole("link").style.getPropertyValue("--lj-st")).toBe("hsl(var(--destructive))");
    expect(document.querySelector(".lj-s-bad")).not.toBeNull();
    cleanup();
    token({ stuck: true, progress: 10 });
    expect(screen.getByRole("link").className).toContain("lj-warn");
    expect(screen.getByRole("link").style.getPropertyValue("--lj-st")).toBe("var(--lj-warning)");
    expect(document.querySelector(".lj-s-warn")).not.toBeNull();
    cleanup();
    token({ state: { tone: "warn", label: "Paused" } });
    expect(screen.getByRole("link").className).toContain("lj-warn");
    expect(document.querySelector(".lj-s")).toHaveClass("lj-s-warn");
    cleanup();
    token({ state: { tone: "info", label: "Downloading" }, progress: 1 });
    expect(screen.getByRole("link").style.getPropertyValue("--lj-st")).toBe("hsl(var(--info))");
    expect(document.querySelector(".lj-s-warn, .lj-s-bad, .lj-s-ok")).toBeNull();
  });

  it("colours the status line green for a finished import, and never moves its bar", () => {
    token({ stage: IMPORTING, landed: true, speedMbps: 5 });
    expect(document.querySelector(".lj-s")).toHaveClass("lj-s-ok");
    expect(document.querySelector(".lj-pb")?.className).not.toContain("lj-moving");
    expect(document.querySelector(".lj-s-text b")?.textContent).toBe("✓ Imported");
  });

  it("puts the percentage of a download in bold and the rest quiet", () => {
    token({ progress: 40, client: "qBittorrent" });
    expect(document.querySelector(".lj-s-text b")?.textContent).toBe("40%");
    expect(document.querySelector(".lj-s-text")?.textContent).toBe("40% · qBittorrent");
  });

  it("draws a pulse beside a status that is being worked on, and none beside one that is not", () => {
    token(searching);
    expect(document.querySelector(".lj-pulse")).not.toBeNull();
    cleanup();
    token({ progress: 3 });
    expect(document.querySelector(".lj-pulse")).toBeNull();
  });

  it("places a card on its station's column and row, and hides one beyond the rows that fit", () => {
    token({}, { place: { column: 2, stations: 5, top: 48, hidden: false } });
    const link = screen.getByRole("link");
    expect(link.style.left).toBe("calc(40% + 6px)");
    expect(link.style.top).toBe("48px");
    expect(link).not.toHaveAttribute("tabindex");
    cleanup();
    token({}, { place: { column: 0, stations: 5, top: 0, hidden: true } });
    expect(screen.getByRole("link").className).toContain("lj-hidden");
    expect(screen.getByRole("link")).toHaveAttribute("tabindex", "-1");
  });

  it("glows a card that has just arrived at its station", () => {
    token({}, { arrivals: new Set(["k1"]) });
    expect(screen.getByRole("link").className).toContain("lj-arrive");
    cleanup();
    token({}, { arrivals: new Set(["other"]) });
    expect(screen.getByRole("link").className).not.toContain("lj-arrive");
  });

  it("tells in its tooltip the release, what started it, why it stopped and the route", () => {
    token({ release: "Heat.1995.1080p-GRP", reasonFull: "Peers gone", reason: "Not moving", startedBy: "seerr", indexer: "NZBgeek" });
    expect(screen.getByRole("link").getAttribute("title")).toBe(`Heat (1995) · Seerr request · Peers gone · NZBgeek`);
    cleanup();
    token({ reason: "Not moving", routeNote: "Asked by Sam" });
    expect(screen.getByRole("link").getAttribute("title")).toBe("Heat (1995) · Not moving · Asked by Sam");
  });

  it("has only the title in its tooltip when there is nothing else to tell", () => {
    token({ progress: 1 });
    expect(screen.getByRole("link").getAttribute("title")).toBe("Heat (1995)");
  });

  it("names the release in the tooltip when the library knows the title", () => {
    const titles = { ...NO_TITLES, movies: new Map([["m1", { title: "Heat", posterUrl: "/p.jpg" }]]), answered: { movies: true, series: true } } as never;
    token({ release: "Heat.1995.1080p-GRP", entity: { kind: "movie", id: "m1" } }, { titles });
    expect(screen.getByRole("link").getAttribute("title")).toContain("Heat.1995.1080p-GRP");
    expect(screen.getByRole("link", { name: /^Heat:/ })).toBeInTheDocument();
  });

  it("draws a detail line's words plain, its figures bold, its release in mono and its right-hand side", () => {
    token({ stage: "grabbed", state: { tone: "ok", label: "Sent" }, sizeBytes: 4e9, client: "qBittorrent", release: "Heat.1995.1080p-GRP" });
    const lines = document.querySelectorAll(".lj-xl");
    expect(lines).toHaveLength(3);
    expect(lines[2].textContent).toBe("Grabbed");
    expect(lines[0].firstElementChild).toHaveClass("lj-rel");
    expect(lines[0].firstElementChild?.textContent).toBe("Heat.1995.1080p-GRP");
    expect(lines[0].querySelector("b")).toBeNull();
    expect(lines[1].firstElementChild).not.toHaveClass("lj-rel");
    expect(lines[1].querySelector("b")?.textContent).toMatch(/GB/);
    expect(lines[1].lastElementChild?.textContent).toBe("to qBittorrent");
    cleanup();
    token({ stage: "processor", state: { tone: "info", label: "Cleaning" }, sizeBytes: 4e9 });
    expect(document.querySelector(".lj-xl > span")?.textContent).toMatch(/GB handed over$/);
    expect(document.querySelector(".lj-xl > span > b")?.textContent).toMatch(/GB$/);
    expect(document.querySelector(".lj-xl > span > span")?.textContent).toBe(" handed over");
  });

  it("draws the detail lines under the bar, and no block when there is nothing to say", () => {
    token({ progress: 40, speedMbps: 3, sizeBytes: 4e9, client: "qBittorrent", indexer: "NZBgeek" });
    expect(document.querySelectorAll(".lj-xl").length).toBeGreaterThan(0);
    cleanup();
    token({ stage: IMPORTED, state: { tone: "ok", label: "In the library" } });
    expect(document.querySelector(".lj-x")).toBeNull();
  });
});

describe("the poster tile", () => {
  it("shows initials on a tinted tile, by its own hue when it is given one", () => {
    const { container } = render(<><PosterTile title="Heat (1995)" kind="movie" /><PosterTile title="Lioness" kind="series" hue={12} /></>);
    const tiles = container.querySelectorAll<HTMLElement>(".lj-tile");
    expect(tiles[0].style.getPropertyValue("--tile-hue")).toBe("205");
    expect(tiles[1].style.getPropertyValue("--tile-hue")).toBe("12");
    expect(tiles[0].textContent).toBe("H");
  });

  it("holds the picture in a 2:3 slot, with the tile as its fallback", () => {
    const { container } = render(<PosterSlot src={null} title="Heat" kind="movie" />);
    expect(container.querySelector(".lj-poster")).not.toBeNull();
    expect(container.querySelector(".lj-tile")?.textContent).toBe("H");
  });
});

describe("a station's count", () => {
  it("shows the figure with thousands grouped", () => {
    render(<Count value={1234} />);
    expect(screen.getAllByText("1,234")).toHaveLength(2);
  });
});
