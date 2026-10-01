import { act, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fullSummary } from "../downloads-fixtures";
import { Band, buildOperations, nextFigure } from "./band";
import { ClientTiles } from "./clients";
import { ConnectionTiles } from "./connection-tiles";
import { applyConnectionEvents, connectionsSummary, connectionWords, sortConnections } from "./connections";
import { statusOf, type CardContext } from "./card";
import { Tile, TileLayoutContext } from "./tile";
import { IndexerTiles, indexerMeta, indexersSummary } from "./indexers";
import { byStage, laneCounts, type JourneyCard, type Stage } from "./journey";
import { MAX_TILE_COLUMNS, tileCardRem, tileColumns, tileDensity } from "./layout";
import { tileName } from "./connections";
import { EMPTY_STATION_REM, journeyMode, MIN_STATION_REM, posterLayout, rowsThatFit, stationDensity, stationLayout, stationWidths } from "./layout";
import { departedFromImporting, sameTitle } from "./motion";
import { arrivedHeading, Shelf } from "./shelf";
import { SpeedCell } from "./speed-chart";
import { allInBy, buildNext, clockTime, doneAbout, journeyHeading, usualTimes, whenText } from "./times";
import { NO_TITLES } from "./titles";
import { Journey, stationsFor } from "./track";
import { barFraction, mergeTurnedDown, REASON_ORDER, turnedDownFit } from "./turned-down";
import { TurnedDownCard } from "./turned-down-card";

const status = vi.hoisted(() => ({ value: "connected" }));
vi.mock("../../../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../../lib/use-signalr")>()),
  useSignalRStatus: () => status.value
}));

const NOW = new Date("2026-10-01T10:00:00");
const inMs = (ms: number) => new Date(NOW.getTime() + ms).toISOString();

function withRouter(node: React.ReactNode) {
  return render(<MemoryRouter>{node}</MemoryRouter>);
}

function setReducedMotion(reduce: boolean) {
  window.matchMedia = ((query: string) => ({ matches: reduce && query.includes("prefers-reduced-motion"), media: query, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false, onchange: null })) as typeof window.matchMedia;
}

const card = (extra: Partial<JourneyCard>): JourneyCard => ({ key: "k", stage: "downloading", title: "Sintel (2010)", state: { tone: "info", label: "Downloading" }, to: "/queue", progress: 43, speedMbps: 6.2, client: "qBittorrent", ...extra });

beforeEach(() => {
  status.value = "connected";
  setReducedMotion(false);
});
afterEach(() => vi.useRealTimers());

describe("the layout rules", () => {
  it("counts the whole card rows a height holds, and always at least one", () => {
    expect(rowsThatFit(300, 88, 6)).toBe(3);
    expect(rowsThatFit(276, 88, 6)).toBe(3);
    expect(rowsThatFit(275, 88, 6)).toBe(2);
    expect(rowsThatFit(900, 88, 6)).toBe(9);
    expect(rowsThatFit(20, 88, 6)).toBe(1);
    expect(rowsThatFit(0, 88, 6)).toBe(1);
  });

  it("keeps the stations side by side while each has room, and stacks them when it does not", () => {
    expect(journeyMode(5 * MIN_STATION_REM * 16, 5, 16)).toBe("columns");
    expect(journeyMode(5 * MIN_STATION_REM * 16 - 1, 5, 16)).toBe("stacked");
    expect(journeyMode(390, 5, 16)).toBe("stacked");
    // Fewer stations (no processor connected) need less room.
    expect(journeyMode(4 * MIN_STATION_REM * 16, 4, 16)).toBe("columns");
    // Nothing measured yet: the columns.
    expect(journeyMode(0, 5, 16)).toBe("columns");
  });

  it("draws full cards while they fit, compact tiles when they do not, and full cards when stacked (the page scrolls)", () => {
    expect(stationDensity(3, 300, 88, 6, "columns")).toBe("full");
    expect(stationDensity(4, 300, 88, 6, "columns")).toBe("compact");
    expect(stationDensity(40, 300, 88, 6, "stacked")).toBe("full");
    expect(stationDensity(40, 0, 88, 6, "columns")).toBe("full");
  });

  it("shows more rows of cards on a taller screen", () => {
    expect(rowsThatFit(1080 * 0.3, 88, 6)).toBeLessThan(rowsThatFit(1440 * 0.3, 88, 6));
  });

  it("gives stations width by need: empty ones narrow, busy ones share the rest by their cards", () => {
    const widths = stationWidths(1400, [0, 0, 13, 4, 4], 16);
    expect(widths.reduce((sum, width) => sum + width, 0)).toBeCloseTo(1400);
    expect(widths[0]).toBe(EMPTY_STATION_REM * 16);
    expect(widths[1]).toBe(EMPTY_STATION_REM * 16);
    expect(widths[2]).toBeGreaterThan(widths[3]);
    expect(widths[3]).toBeCloseTo(widths[4]);
    for (const width of widths.slice(2)) expect(width).toBeGreaterThanOrEqual(MIN_STATION_REM * 16);
    // Nothing anywhere, or everywhere: equal shares. Too narrow to shrink anything: equal shares.
    expect(stationWidths(1000, [0, 0, 0, 0], 16)).toEqual([250, 250, 250, 250]);
    expect(stationWidths(1000, [2, 2, 2, 2], 16)).toEqual([250, 250, 250, 250]);
    expect(stationWidths(300, [0, 5, 5], 16).every((width) => width === 100)).toBe(true);
  });

  it("a busy station shows its cards in as many columns as its width allows before it scrolls", () => {
    // Plenty of height for 4 full cards: 13 cards in a wide station are compact rows in several columns.
    const wide = stationLayout(13, 700, 420, 88, 16, "columns");
    expect(wide.density).toBe("compact");
    expect(wide.columns).toBeGreaterThan(1);
    // A narrow station keeps one column.
    expect(stationLayout(13, 220, 420, 88, 16, "columns").columns).toBe(1);
    // Cards that fit as full cards across a wide station stay full, side by side only as needed.
    expect(stationLayout(4, 700, 420, 88, 16, "columns")).toEqual({ density: "full", columns: 1 });
    expect(stationLayout(8, 700, 420, 88, 16, "columns")).toMatchObject({ density: "full", columns: 2 });
    expect(stationLayout(40, 700, 420, 88, 16, "stacked")).toEqual({ density: "full", columns: 1 });
  });

  it("sizes posters to the shelf: 2:3, one row on a short shelf, more rows on a tall one, never absurdly small or large", () => {
    const short = posterLayout(900, 290, 16);
    const tall = posterLayout(900, 700, 16);
    expect(short.rows).toBe(1);
    expect(tall.rows).toBeGreaterThan(short.rows);
    for (const layout of [short, tall]) {
      expect(layout.height).toBe(Math.round(layout.width * 1.5));
      expect(layout.width).toBeGreaterThanOrEqual(4.4 * 16);
      expect(layout.width).toBeLessThanOrEqual(16 * 16);
      expect(layout.capacity).toBe(layout.rows * layout.perRow);
    }
    // The rows that are drawn really fit the height they were given.
    expect(tall.rows * tall.itemHeight + (tall.rows - 1) * 14).toBeLessThanOrEqual(700);
    // A wider shelf holds more across.
    expect(posterLayout(1800, 290, 16).perRow).toBeGreaterThan(short.perRow);
  });

  it("fills the shelf: few posters are one row of the tallest posters, many take more rows only when needed", () => {
    const few = posterLayout(1100, 454, 16, 5);
    expect(few.rows).toBe(1);
    expect(few.width).toBeGreaterThan(posterLayout(1100, 454, 16).width);
    expect(few.capacity).toBeGreaterThanOrEqual(5);
    const many = posterLayout(1100, 454, 16, 30);
    expect(many.rows).toBeGreaterThan(1);
    expect(many.rows * many.itemHeight + (many.rows - 1) * 14).toBeLessThanOrEqual(454);
    const mid = posterLayout(600, 454, 16, 9);
    expect(mid.capacity).toBeGreaterThanOrEqual(9);
  });
});

describe("what comes next", () => {
  const summary = () => {
    const copy = structuredClone(fullSummary);
    copy.automation!.libraries = [{ libraryId: "l1", name: "Movies", mediaType: "movies", status: "active", nextSearchUtc: inMs(12 * 60_000 + 4_000), nextMissingSearchUtc: null, nextUpgradeSearchUtc: null, lastCompletedUtc: inMs(-48 * 60_000), lastError: null }];
    copy.automation!.tasks = [
      { key: "rss.sync", name: "Watch indexers for new releases", lastResult: "completed", lastCompletedUtc: inMs(-60_000), nextRunUtc: inMs(9 * 60_000) },
      { key: "health.checks", name: "Check health", lastResult: "completed", lastCompletedUtc: inMs(-3 * 60_000), nextRunUtc: inMs(27 * 60_000) },
      { key: "never", name: "Not scheduled at all", lastResult: "not-run", lastCompletedUtc: null, nextRunUtc: null }
    ];
    return copy;
  };
  const episode = (id: string, hours: number) => ({ seriesId: "s1", title: "Lioness", startYear: 2023, posterUrl: null, episodeId: id, seasonNumber: 3, episodeNumber: 8, episodeTitle: "x", airDateUtc: inMs(hours * 3_600_000) }) as never;

  it("merges scheduled jobs and episodes about to air into one list, soonest first, and leaves out what is not scheduled", () => {
    const items = buildNext(summary(), [episode("e1", 1.5), episode("e2", -3)], NOW.getTime());
    expect(items.map((item) => item.label)).toEqual(["Watch indexers for new releases", "Search Movies", "Check health", "Lioness S03E08 airs"]);
    expect(items.map((item) => item.kind)).toEqual(["job", "job", "job", "airing"]);
    // An episode that aired hours ago is not next.
    expect(items.some((item) => item.key === "airs:e2")).toBe(false);
  });

  it("puts a job that is running now first", () => {
    const running = summary();
    running.automation!.libraries[0] = { ...running.automation!.libraries[0], status: "running" };
    expect(buildNext(running, [], NOW.getTime())[0]).toMatchObject({ label: "Search Movies", running: true });
  });

  it("says a countdown under an hour and a clock time beyond it, with the day when it is not today", () => {
    const now = NOW.getTime();
    expect(whenText(now + 252_000, now)).toBe("4:12");
    expect(whenText(now + 5_040_000, now)).toBe("11:24 am");
    expect(whenText(now + 5 * 3_600_000, now)).toBe("3:00 pm");
    expect(whenText(now + 24 * 3_600_000, now)).toBe("tomorrow 10:00 am");
    expect(whenText(now - 1000, now)).toBe("now");
    expect(clockTime(new Date("2026-10-01T00:05:00").getTime())).toBe("12:05 am");
    expect(clockTime(new Date("2026-10-01T15:34:00").getTime())).toBe("3:34 pm");
  });

  it("shows the countdown to the first in the figure with the others under it, and counts down by the second", () => {
    vi.useFakeTimers({ now: NOW });
    withRouter(<Band summary={summary()} samples={[]} operations={[]} upcoming={[episode("e1", 3.5)]} />);
    const next = screen.getByRole("region", { name: "Next" });
    expect(within(next).getByText("9:00")).toBeInTheDocument();
    expect(within(next).getByText("Watch indexers for new releases")).toBeInTheDocument();
    expect(next.textContent).toContain("then Search Movies");
    expect(next.textContent).toContain("then Lioness S03E08 airs");
    expect(next.textContent).toContain("1:30 pm");
    act(() => { vi.advanceTimersByTime(3_000); });
    expect(within(next).getByText("8:57")).toBeInTheDocument();
  });

  it("figures: running, due now, a countdown or a clock time", () => {
    const now = NOW.getTime();
    const item = { key: "k", label: "x", at: now, last: null, running: false, to: "/", kind: "job" as const };
    expect(nextFigure({ ...item, running: true }, now).kind).toBe("running");
    expect(nextFigure({ ...item, at: now - 1 }, now).kind).toBe("due");
    expect(nextFigure({ ...item, at: now + 60_000 }, now)).toEqual({ text: "1:00", kind: "count" });
    expect(nextFigure({ ...item, at: now + 7_200_000 }, now).kind).toBe("clock");
  });

  it("says something true when nothing is scheduled", () => {
    withRouter(<Band summary={undefined} samples={[]} operations={[]} upcoming={[]} />);
    expect(screen.getByText("nothing is scheduled")).toBeInTheDocument();
  });
});

describe("working on now", () => {
  it("counts what is running, shows the two newest with a bar where it knows, and says how many more are in Activity", () => {
    const ops = buildOperations(
      [{ key: "search:l1", kind: "search", text: "Searching Movies for what is missing or could be better", progress: 30, detail: "12 of 40", to: "/search-cycles" }, { key: "job:a", kind: "jobs", text: "Reading your files", to: "/activity" }],
      [card({ key: "p", stage: "processor", processor: "FileFlows", state: { tone: "info", label: "Being cleaned" } }), card({ key: "i", stage: "importing", title: "Moon (2009)", state: { tone: "info", label: "Importing now" } }), card({ key: "w", stage: "importing", title: "Waiting (2001)", state: { tone: "idle", label: "Downloaded" } })],
      NO_TITLES
    );
    expect(ops.map((op) => op.kind)).toEqual(["search", "processor", "import", "jobs"]);
    expect(ops[1]).toMatchObject({ lead: "FileFlows is working on", name: "Sintel (2010)" });
    withRouter(<Band summary={fullSummary} samples={[]} operations={ops} upcoming={[]} />);
    const region = screen.getByRole("region", { name: "Working on now" });
    expect(within(region).getByText("4")).toBeInTheDocument();
    expect(within(region).getByText("things running")).toBeInTheDocument();
    expect(within(region).getByText("12 of 40")).toBeInTheDocument();
    expect(region.querySelector<HTMLElement>(".live-fill")?.style.width).toBe("30%");
    expect(within(region).getByRole("link", { name: "2 more in Activity" })).toHaveAttribute("href", "/activity");
    // Only two operations are drawn.
    expect(within(region).getAllByRole("listitem")).toHaveLength(2);
  });

  it("does not name a processor failure as something it is working on", () => {
    expect(buildOperations([], [card({ stage: "processor", state: { tone: "bad", label: "Processor failed" } })], NO_TITLES)).toEqual([]);
  });

  it("says when the last search was when nothing is running", () => {
    const idle = structuredClone(fullSummary);
    idle.automation!.lastSearchCycle = { libraryId: "l1", libraryName: "Movies", mediaType: "movies", status: "completed", planned: 14, queued: 2, skipped: 12, startedUtc: inMs(-49 * 60_000), completedUtc: inMs(-48 * 60_000) };
    withRouter(<Band summary={idle} samples={[]} operations={[]} upcoming={[]} />);
    expect(screen.getByText(/The last search was Movies/).textContent).toContain("14 checked, 2 sent to a client");
  });
});

describe("speed", () => {
  it("draws a trace with a reading even when nothing has ever been stored, and says the unit beside the figure", () => {
    const { container } = withRouter(<SpeedCell samples={[]} summary={undefined} />);
    expect(container.querySelector("svg path")).not.toBeNull();
    expect(container.textContent).toContain("0 MB/s");
    expect(container.textContent).not.toMatch(/NaN|undefined/);
  });

  it("extends the trace with the newest reading and says download and upload apart", () => {
    const summary = structuredClone(fullSummary);
    summary.downloads!.downloadMbps = 14.2;
    summary.downloads!.uploadMbps = 1.3;
    summary.downloads!.readingUtc = new Date().toISOString();
    const { container } = withRouter(<SpeedCell samples={[{ capturedUtc: inMs(-3_600_000), speedMbps: 4, activeCount: 1, uploadMbps: 0 }]} summary={summary} />);
    expect(container.textContent).toContain("14 MB/s");
    expect(container.textContent).toContain("down");
    expect(container.textContent).toContain("1.3 MB/s");
    expect(screen.getByRole("img").getAttribute("aria-label")).toMatch(/Download peaked at 14 MB\/s/);
  });
});

describe("what a journey card says", () => {
  it("looks on the indexers, or says Seerr asked", () => {
    expect(statusOf(card({ stage: "requested" }), 5).text).toBe("Looking on 5 indexers");
    expect(statusOf(card({ stage: "requested" }), 1).text).toBe("Looking on 1 indexer");
    expect(statusOf(card({ stage: "requested", startedBy: "seerr" }), 5).text).toBe("Asked for in Seerr · looking");
    expect(statusOf(card({ stage: "requested" }), 0).text).toBe("Looking for it");
  });

  it("says the quality and where it came from, the progress and speed, and who is working on it", () => {
    expect(statusOf(card({ stage: "grabbed", quality: "Bluray 1080p", indexer: "NZBgeek" }), 5).text).toBe("Bluray 1080p · from NZBgeek");
    expect(statusOf(card({}), 5).text).toBe("43% · 6.2 MB/s · qBittorrent");
    expect(statusOf(card({ stage: "processor", processor: "Weir", state: { tone: "info", label: "Being cleaned" } }), 5).text).toBe("Weir is working on it");
    expect(statusOf(card({ stage: "processor", processor: null, state: { tone: "info", label: "Being cleaned" } }), 5).text).toBe("The processor is working on it");
  });

  it("goes amber and says it is says it is not moving while a download is stuck", () => {
    const stuck = statusOf(card({ stuck: true, state: { tone: "warn", label: "Stuck" } }), 5);
    expect(stuck).toEqual({ text: "Stuck at 43% · not moving", tone: "warn", pulse: true });
    expect(statusOf(card({ stuck: true, progress: 0 }), 5).text).toBe("Stuck · not moving");
  });
});

describe("when everything should be in", () => {
  it("is the latest finish time the clients give, leaving out stuck titles and titles with no reading", () => {
    const cards = [card({ key: "a", finishAtMs: 1000 }), card({ key: "b", finishAtMs: 5000 }), card({ key: "c", finishAtMs: 9000, stuck: true }), card({ key: "d" }), card({ key: "e", stage: "imported", finishAtMs: 99_000 })];
    expect(allInBy(cards)).toBe(5000);
    expect(allInBy([card({})])).toBeUndefined();
  });

  it("writes the header with the time, and without it when no reading says", () => {
    const by = new Date("2026-10-01T14:28:00").getTime();
    expect(journeyHeading(8, by)).toBe("8 on their way to your library · all in by about 2:28 pm");
    expect(journeyHeading(8, undefined)).toBe("8 on their way to your library");
    expect(journeyHeading(0, by)).toBe("nothing on its way right now");
  });

  it("says a card's own finish time", () => {
    expect(doneAbout(new Date("2026-10-01T14:14:00").getTime())).toBe("done about 2:14 pm");
    expect(doneAbout(undefined)).toBeNull();
  });
});

describe("how long a station usually takes today", () => {
  const dispatch = (id: string, minutes: { grab: number; down: number; imp: number }, doneHoursAgo = 1) => {
    const done = NOW.getTime() - doneHoursAgo * 3_600_000;
    const imported = done;
    const importDetected = imported - minutes.imp * 60_000;
    const detected = importDetected - minutes.down * 60_000;
    const created = detected - minutes.grab * 60_000;
    return { id, createdUtc: new Date(created).toISOString(), detectedUtc: new Date(detected).toISOString(), importDetectedUtc: new Date(importDetected).toISOString(), importCompletedUtc: new Date(imported).toISOString() } as never;
  };
  const handoff = (id: string, minutes: number) => ({ id, status: "completed", createdUtc: inMs(-minutes * 60_000 - 3_600_000), updatedUtc: inMs(-3_600_000) }) as never;

  it("averages today's finished journeys once there are at least three, and says nothing before", () => {
    const some = [dispatch("1", { grab: 1, down: 10, imp: 1 }), dispatch("2", { grab: 3, down: 14, imp: 2 })];
    expect(usualTimes(some, [], NOW.getTime())).toEqual({});
    const three = [...some, dispatch("3", { grab: 2, down: 12, imp: 3 })];
    expect(usualTimes(three, [handoff("a", 4), handoff("b", 6), handoff("c", 8)], NOW.getTime())).toEqual({ grabbed: "usually 2 min", downloading: "usually 12 min", importing: "usually 2 min", processor: "usually 6 min" });
  });

  it("only counts journeys finished today", () => {
    const old = [dispatch("1", { grab: 1, down: 10, imp: 1 }, 30), dispatch("2", { grab: 1, down: 10, imp: 1 }, 30), dispatch("3", { grab: 1, down: 10, imp: 1 }, 30)];
    expect(usualTimes(old, [], NOW.getTime())).toEqual({});
  });

  it("is on the station that has it, and left off the others", () => {
    const grouped = byStage([]);
    withRouter(<Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={{ titles: NO_TITLES, arrivals: new Set(), indexers: 3 }} hasProcessor usual={{ downloading: "usually 12 min" }} by={undefined} lastArrival={null} />);
    expect(screen.getByRole("link", { name: "Downloading: 0" }).textContent).toContain("usually 12 min");
    expect(screen.getByRole("link", { name: "Grabbed: 0" }).textContent).not.toContain("usually");
  });
});

describe("the journey", () => {
  const context: CardContext = { titles: NO_TITLES, arrivals: new Set(), indexers: 4 };
  const render5 = (cards: JourneyCard[], hasProcessor = true) => {
    const grouped = byStage(cards);
    return withRouter(<Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={context} hasProcessor={hasProcessor} usual={{}} by={new Date("2026-10-01T14:28:00").getTime()} lastArrival={null} />);
  };
  const cards = [
    card({ key: "r", stage: "requested", title: "Shogun", startedBy: "seerr", state: { tone: "info", label: "Requested" } }),
    card({ key: "d1", title: "Dune (2024)", startedBy: "you", finishAtMs: new Date("2026-10-01T14:14:00").getTime() }),
    card({ key: "d2", title: "Stuck One (2001)", stuck: true, state: { tone: "warn", label: "Stuck" }, progress: 43, speedMbps: 0 }),
    card({ key: "p", stage: "processor", title: "Sintel (2010)", processor: "Weir", state: { tone: "info", label: "Being cleaned" } })
  ];

  it("has a station for the processor only when one is connected", () => {
    expect(stationsFor(true)).toContain("processor");
    expect(stationsFor(false)).not.toContain("processor");
    render5(cards, false);
    expect(screen.queryByRole("group", { name: "Processing" })).toBeNull();
    expect(screen.getByRole("group", { name: "Importing" })).toBeInTheDocument();
  });

  it("says how many are on their way and when they should all be in", () => {
    render5(cards);
    const journey = screen.getByRole("region", { name: "The journey" });
    expect(within(journey).getByText("4 on their way to your library · all in by about 2:28 pm")).toBeInTheDocument();
    expect(within(journey).getByRole("link", { name: "Transfers: The journey" })).toHaveAttribute("href", "/queue");
  });

  it("puts each title in its station as a card, a stuck one amber, and says why each is moving", () => {
    render5(cards);
    const downloading = screen.getByRole("list", { name: "Downloading titles" });
    expect(within(downloading).getByRole("link", { name: "Stuck One (2001): Stuck at 43% · not moving" })).toBeInTheDocument();
    expect(within(downloading).getByText("done about 2:14 pm")).toBeInTheDocument();
    expect(within(downloading).getByText("You asked")).toBeInTheDocument();
    // A card that came from Seerr says so; a request card says it in its line and not twice.
    const requested = screen.getByRole("list", { name: "Requested titles" });
    expect(within(requested).getByRole("link", { name: "Shogun: Asked for in Seerr · looking" })).toBeInTheDocument();
    expect(within(requested).queryByText("Seerr")).toBeNull();
    // A card with no recorded reason has no tag rather than a guess.
    expect(within(screen.getByRole("list", { name: "Processing titles" })).queryByText(/You asked|Seerr|From a list|Upgrade hunt|Missing episode/)).toBeNull();
    expect(screen.getByRole("link", { name: /Stuck One/ }).querySelector(".live-blink")).not.toBeNull();
  });

  it("tags a download that came from Seerr, a list, an upgrade hunt or a missing episode", () => {
    render5([card({ key: "a", title: "A (2001)", startedBy: "seerr" }), card({ key: "b", title: "B (2002)", startedBy: "list" }), card({ key: "c", title: "C (2003)", startedBy: "upgrade" }), card({ key: "d", title: "D (2004)", startedBy: "missing" })]);
    for (const words of ["Seerr", "From a list", "Upgrade hunt", "Missing episode"]) expect(screen.getByText(words)).toBeInTheDocument();
  });

  it("opens the title's page from any card", () => {
    render5([card({ key: "a", title: "A (2001)", to: "/movies/m1" })]);
    expect(screen.getByRole("link", { name: /^A \(2001\)/ })).toHaveAttribute("href", "/movies/m1");
  });

  it("says one calm line when nothing is moving, with the last arrival", () => {
    const grouped = byStage([]);
    withRouter(<Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={context} hasProcessor usual={{}} by={undefined} lastArrival={{ title: "Moon (2009)", at: new Date().toISOString() }} />);
    expect(screen.getByText(/Nothing moving right now · last arrival Moon \(2009\)/)).toBeInTheDocument();
  });

  it("never says and N more: when the cards do not fit the station they become compact tiles, every title still there", () => {
    const rect = HTMLElement.prototype.getBoundingClientRect;
    const observer = globalThis.ResizeObserver;
    globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
    HTMLElement.prototype.getBoundingClientRect = function sized() {
      const name = typeof this.className === "string" ? this.className : "";
      const make = (width: number, height: number) => ({ width, height, top: 0, left: 0, right: width, bottom: height, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
      if (name.includes("w-px")) return make(1, 88);
      if (name.includes("inset-0")) return make(1100, 300);
      if (name.includes("relative flex min-h-0 flex-1 flex-col")) return make(1200, 400);
      return make(0, 0);
    };
    try {
      const many = Array.from({ length: 12 }, (_, n) => card({ key: `m${n}`, title: `Film ${n} (2001)` }));
      render5(many);
      const station = screen.getByRole("list", { name: "Downloading titles" }).parentElement!;
      expect(station).toHaveAttribute("data-cards", "compact");
      expect(screen.getAllByRole("link", { name: /^Film [0-9]+ \(2001\)/ })).toHaveLength(12);
      expect(document.body.textContent).not.toMatch(/and [0-9]+ more/);
      // Few enough to fit stay full cards.
      expect(screen.getByRole("list", { name: "Downloading titles" }).querySelector("[data-compact]")).not.toBeNull();
    } finally {
      HTMLElement.prototype.getBoundingClientRect = rect;
      globalThis.ResizeObserver = observer;
    }
  });

  it("stacks the stations under one another when the journey is narrow, each with its own cards", () => {
    const rect = HTMLElement.prototype.getBoundingClientRect;
    const observer = globalThis.ResizeObserver;
    globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
    HTMLElement.prototype.getBoundingClientRect = function sized() {
      const name = typeof this.className === "string" ? this.className : "";
      const make = (width: number, height: number) => ({ width, height, top: 0, left: 0, right: width, bottom: height, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
      if (name.includes("relative flex min-h-0 flex-1 flex-col")) return make(360, 600);
      return make(0, 0);
    };
    try {
      render5(cards);
      // Each station's heading is followed by its own cards, and a station with nothing is left out.
      const stacked = Array.from(document.querySelectorAll('[data-station]'));
      expect(stacked.map((element) => element.getAttribute("data-station"))).toEqual(["requested", "downloading", "processor"]);
      expect(within(stacked[1] as HTMLElement).getByRole("group", { name: "Downloading" })).toBeInTheDocument();
      expect(stacked.every((element) => element.getAttribute("data-cards") === "full")).toBe(true);
    } finally {
      HTMLElement.prototype.getBoundingClientRect = rect;
      globalThis.ResizeObserver = observer;
    }
  });

  it("only shimmers a download bar while bytes are really moving", () => {
    const { container, unmount } = render5([card({ key: "a" })]);
    expect(container.querySelector(".live-shimmer")).not.toBeNull();
    unmount();
    const idle = render5([card({ key: "a", speedMbps: 0 })]);
    expect(idle.container.querySelector(".live-shimmer")).toBeNull();
  });

  it("glows a card that has just changed station, and does not when motion is reduced", () => {
    const grouped = byStage([card({ key: "a" })]);
    const draw = () => withRouter(<Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={{ ...context, arrivals: new Set(["a"]) }} hasProcessor usual={{}} by={undefined} lastArrival={null} />);
    expect(draw().container.querySelector(".live-card-arrive")).not.toBeNull();
    setReducedMotion(true);
    expect(draw().container.querySelector(".live-card-arrive")).toBeNull();
  });
});

describe("flying into Arrived today", () => {
  it("finds the cards that left Importing because the title was imported, and not those that moved on", () => {
    const before = new Map<string, Stage>([["a", "importing"], ["b", "importing"], ["c", "downloading"]]);
    const now = [card({ key: "b", stage: "importing" })];
    expect(departedFromImporting(before, now)).toEqual(["a"]);
  });

  it("matches the arrival to the card by library title, or by release", () => {
    const arrival = (extra: object) => ({ entity: undefined, release: null, ...extra }) as never;
    expect(sameTitle({ entity: { kind: "movie", id: "m1" }, release: "A.2001" }, arrival({ entity: { kind: "movie", id: "m1" } }))).toBe(true);
    expect(sameTitle({ entity: undefined, release: "Sintel.2010.1080p" }, arrival({ release: "sintel 2010 1080p" }))).toBe(true);
    expect(sameTitle({ entity: { kind: "movie", id: "m1" }, release: "A.2001" }, arrival({ entity: { kind: "movie", id: "m2" }, release: "B.2002" }))).toBe(false);
    expect(sameTitle({ entity: undefined, release: undefined }, arrival({}))).toBe(false);
  });
});

describe("arrived today", () => {
  const item = (key: string, extra: object = {}) => ({ key, entity: { kind: "movie", id: key }, fallbackTitle: `Film ${key} (2001)`, release: null, quality: "WEB 1080p", previousQuality: null, upgraded: false, sizeBytes: null, at: new Date().toISOString(), requestedBy: null, to: `/movies/${key}`, ...extra }) as never;

  it("says the titles, the upgrades and the space added", () => {
    expect(arrivedHeading(12, 4, 54 * 1024 ** 3)).toBe("12 titles · 4 upgraded · 54 GB added");
    expect(arrivedHeading(1, 0, 0)).toBe("1 title");
  });

  it("shows each arrival as a poster with its quality, and an upgrade as the quality it replaced; the newest has the ring", () => {
    withRouter(<Shelf items={[item("1"), item("2", { upgraded: true, previousQuality: "WEB 720p", quality: "Bluray 1080p" })]} titles={NO_TITLES} newest={new Set(["1"])} heading="2 titles" />);
    expect(screen.getByText("WEB 720p → Bluray 1080p")).toBeInTheDocument();
    const first = screen.getByRole("link", { name: /^Film 1 \(2001\): in the library/ });
    expect(first.querySelector("[data-arrival-poster='1']")?.className).toContain("shadow-[0_0_0_2px");
    expect(screen.getByRole("link", { name: /^Film 2 \(2001\)/ }).querySelector("[data-arrival-poster='2']")?.className).not.toContain("shadow-[0_0_0_2px");
    expect(screen.getByRole("link", { name: /^Film 1/ })).toHaveAttribute("href", "/movies/1");
  });

  it("says something true when nothing has arrived", () => {
    withRouter(<Shelf items={[]} titles={NO_TITLES} newest={new Set()} heading="0 titles" />);
    expect(screen.getByText("Nothing has arrived yet today.")).toBeInTheDocument();
  });
});

describe("turned down today", () => {
  const base = {
    total: 43,
    reasons: [{ group: "language", label: "Not in a language you want", count: 14 }, { group: "notBetter", label: "No better than what you have", count: 11 }, { group: "tooBig", label: "Bigger than your size limit", count: 9 }, { group: "fewSeeders", label: "Too few people sharing it", count: 5 }, { group: "fake", label: "Fake or mislabelled", count: 4 }, { group: "other", label: "Other reasons", count: 0 }],
    latest: [{ release: "Dune.2024.iTA.1080p", group: "language", label: "Not in a language you want", title: "Dune", entityType: "movie", entityId: "m1", atUtc: inMs(-60_000) }]
  } as never;

  it("groups the reasons in their order, one bar each against the biggest, and the newest releases under them", () => {
    const view = mergeTurnedDown(base, [], inMs(0));
    expect(view.reasons.map((reason) => reason.group)).toEqual(["language", "notBetter", "tooBig", "fewSeeders", "fake", "notQuality", "tooSmall", "rules", "tooOld", "blocklisted", "other"]);
    expect([...view.reasons].map((reason) => reason.group).sort()).toEqual([...REASON_ORDER].sort());
    expect(barFraction(7, view.reasons)).toBeCloseTo(0.5);
    expect(barFraction(0, [{ count: 0 }])).toBe(0);
    withRouter(<TurnedDownCard view={view} lit={new Set()} />);
    const card = screen.getByRole("region", { name: "Turned down today" });
    expect(within(card).getByText("43 releases")).toBeInTheDocument();
    for (const words of ["Not in a language you want", "No better than what you have", "Bigger than your size limit", "Too few people sharing it", "Fake or mislabelled", "Other reasons"]) expect(within(card).getAllByText(words).length).toBeGreaterThan(0);
    expect((card.querySelector('[data-reason="language"] span[aria-hidden] span') as HTMLElement).style.width).toBe("100%");
    expect((card.querySelector('[data-reason="notBetter"] span[aria-hidden] span') as HTMLElement).style.width).toBe("79%");
    expect(within(card).getByRole("link", { name: /Dune.2024.iTA.1080p/ })).toHaveAttribute("href", "/movies/m1");
  });

  it("draws only the whole rows the height holds: fewer bars on a short card, the newest releases only where there is room", () => {
    expect(turnedDownFit(0, 16, 6, 20)).toEqual({ reasons: 6, latest: 20 });
    expect(turnedDownFit(140, 16, 6, 20)).toEqual({ reasons: 2, latest: 0 });
    expect(turnedDownFit(300, 16, 6, 20)).toEqual({ reasons: 6, latest: 0 });
    const tall = turnedDownFit(520, 16, 6, 20);
    expect(tall.reasons).toBe(6);
    expect(tall.latest).toBeGreaterThan(2);
    expect(turnedDownFit(520, 16, 6, 1).latest).toBe(1);
    expect(turnedDownFit(20, 16, 6, 20).reasons).toBe(1);
  });

  it("adds a release pushed since the counts, once, and ignores one the counts already hold", () => {
    const pushed = [
      { atUtc: inMs(5_000), title: "Moon", entityType: "movie", entityId: "m2", items: [{ release: "Moon.FRENCH", group: "language", label: "Not in a language you want" }] },
      { atUtc: inMs(-5_000), items: [{ release: "Old", group: "fake", label: "Fake or mislabelled" }] }
    ] as never;
    const view = mergeTurnedDown(base, pushed, inMs(0));
    expect(view.total).toBe(44);
    expect(view.reasons.find((reason) => reason.group === "language")?.count).toBe(15);
    expect(view.reasons.find((reason) => reason.group === "fake")?.count).toBe(4);
    expect(view.latest[0]).toMatchObject({ release: "Moon.FRENCH", title: "Moon" });
  });

  it("flashes the bar of a decision just made, and says something true when nothing has been turned down", () => {
    const view = mergeTurnedDown(base, [], inMs(0));
    const { container } = withRouter(<TurnedDownCard view={view} lit={new Set(["fake"])} />);
    expect(container.querySelector('[data-reason="fake"]')).toHaveAttribute("data-hit");
    expect(container.querySelector('[data-reason="language"]')).not.toHaveAttribute("data-hit");
    const empty = mergeTurnedDown(null, [], null);
    expect(empty.total).toBe(0);
    expect(empty.reasons).toHaveLength(REASON_ORDER.length);
    withRouter(<TurnedDownCard view={empty} lit={new Set()} />);
    expect(screen.getByText("Nothing has been turned down yet today.")).toBeInTheDocument();
  });
});

describe("the indexer tiles", () => {
  const indexer = (id: string, extra: Record<string, unknown> = {}) => ({ id, name: id, isEnabled: true, priority: 0, healthStatus: "healthy", lastHealthLatencyMs: 320, rateLimitedUntilUtc: null, ...extra }) as never;
  const board = { indexers: [{ id: "NZBgeek", name: "NZBgeek", totalQueries: 46, failedQueries: 0, successfulGrabs: 1, lastQueryUtc: null }, { id: "Nyaa", name: "Nyaa", totalQueries: 5, failedQueries: 5, successfulGrabs: 0, lastQueryUtc: null }] };
  const list = [indexer("Zeta"), indexer("NZBgeek", { queryLimit: 100, limitsUnit: "day", queryLimitStatus: { allowed: true, limit: 100, count: 46, resetsAtUtc: null, unit: "day" } }), indexer("Nyaa", { healthStatus: "unreachable" }), indexer("Old", { isEnabled: false })];

  it("says what each did today, the limit where one is set, and a switched-off one is grey and last", () => {
    withRouter(<IndexerTiles indexers={list} scoreboard={board} summary={undefined} now={NOW.getTime()} />);
    const group = screen.getByRole("region", { name: "Indexers" });
    const names = within(group).getAllByRole("link").map((link) => link.getAttribute("aria-label")?.split(":")[0]).filter((name) => name && !name.startsWith("Manage"));
    expect(names).toEqual(["Nyaa", "NZBgeek", "Zeta", "Old"]);
    expect(within(group).getByRole("link", { name: /^NZBgeek: 46 of 100 today · 320 ms/ })).toBeInTheDocument();
    expect(within(group).getByRole("link", { name: /^Nyaa: not answering · 5 today/ })).toHaveAttribute("href", "/?tab=system");
    expect(within(group).getByRole("link", { name: /^Old: switched off/ })).toHaveAttribute("data-state", "off");
  });

  it("counts answering, switched off and the queries today in the header", () => {
    const today = new Map(board.indexers.map((row) => [row.id, row]));
    expect(indexersSummary(list, today as never)).toBe("2 answering · 1 off · 51 today");
    expect(indexerMeta(indexer("x", { rateLimitedUntilUtc: new Date(NOW.getTime() + 60_000).toISOString() }), undefined, NOW.getTime()).text).toMatch(/^asked too often, waiting/);
    expect(indexerMeta(indexer("x", { lastHealthLatencyMs: 2500 }), undefined, NOW.getTime()).tone).toBe("warn");
    // An hourly limit is not a count of today, so only the day's queries are said.
    expect(indexerMeta(indexer("x", { queryLimit: 20, limitsUnit: "hour" }), { totalQueries: 7 } as never, NOW.getTime()).text).toBe("7 today · 320 ms");
  });
});

describe("the download client tiles", () => {
  it("shows a tile each with its speed and what it is seeding, and the header counts both", () => {
    const row = (id: string, extra: Record<string, unknown> = {}) => ({ id, name: id, protocol: "torrent", reachable: true, paused: false, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "healthy", active: 0, queued: 0, ...extra }) as never;
    withRouter(<ClientTiles rows={[row("qBittorrent"), row("SABnzbd")]} all={[{ id: "qBittorrent", name: "qBittorrent", isEnabled: true }, { id: "SABnzbd", name: "SABnzbd", isEnabled: true }] as never} speeds={new Map([["qBittorrent", 6.2]])} scaleMbps={12} seeding={new Map([["qBittorrent", 8], ["SABnzbd", 0]])} />);
    const group = screen.getByRole("region", { name: "Download clients" });
    expect(within(group).getByText("1 moving · 8 seeding")).toBeInTheDocument();
    expect(within(group).getByRole("link", { name: "qBittorrent: moving 6.2 MB/s, seeding 8" })).toBeInTheDocument();
    expect(within(group).getByRole("link", { name: "SABnzbd: connected, idle" })).toBeInTheDocument();
  });
});

describe("the connection tiles", () => {
  const connection = (kind: string, name: string, countToday: number, extra: object = {}) => ({ kind, id: name, name, enabled: true, healthy: true, countToday, busy: 0, ...extra }) as never;
  const all = [connection("lists", "Trakt", 2), connection("processor", "Weir", 6, { busy: 1 }), connection("subtitles", "OpenSubtitles", 12), connection("requests", "Seerr", 3), connection("mediaServer", "Plex", 5), connection("processor", "FileFlows", 0), connection("mediaServer", "Old Plex", 0, { enabled: false })];

  it("says each one's role and what it did today", () => {
    expect(connectionWords(connection("processor", "Weir", 6, { busy: 1 }))).toBe("Processor · working on 1");
    expect(connectionWords(connection("processor", "FileFlows", 0))).toBe("Processor · idle");
    expect(connectionWords(connection("mediaServer", "Plex", 5))).toBe("Media server · 5 sent today");
    expect(connectionWords(connection("requests", "Seerr", 3))).toBe("Requests · 3 today");
    expect(connectionWords(connection("subtitles", "OpenSubtitles", 12))).toBe("Subtitles · 12 fetched today");
    expect(connectionWords(connection("lists", "Trakt", 2))).toBe("Lists · watching 2");
    expect(connectionWords(connection("lists", "Trakt", 2, { enabled: false }))).toBe("Lists · switched off");
    expect(connectionWords(connection("lists", "Trakt", 2, { healthy: false }))).toBe("Lists · not answering");
  });

  it("orders them by role with the switched-off ones last, and counts them in the header", () => {
    expect(sortConnections(all).map((item) => item.name)).toEqual(["FileFlows", "Weir", "Plex", "Seerr", "OpenSubtitles", "Trakt", "Old Plex"]);
    expect(connectionsSummary(all)).toBe("6 connected · 1 off");
    expect(connectionsSummary([...all, connection("lists", "X", 0, { healthy: false })])).toContain("1 not answering");
    expect(connectionsSummary([])).toBe("nothing connected yet");
  });

  it("lays a newer pushed count over the stored one, ignores an older one and adds one it has not seen", () => {
    const used = new Map([["mediaServer:Plex", { kind: "mediaServer", id: "Plex", name: "Plex", countToday: 9, busy: 0, atUtc: inMs(5_000) }], ["requests:Seerr", { kind: "requests", id: "Seerr", name: "Seerr", countToday: 99, busy: 0, atUtc: inMs(-5_000) }], ["lists:New", { kind: "lists", id: "New", name: "New", countToday: 1, busy: 0, atUtc: inMs(5_000) }]]) as never;
    const merged = applyConnectionEvents(all, used, inMs(0));
    expect(merged.find((item) => item.name === "Plex")?.countToday).toBe(9);
    expect(merged.find((item) => item.name === "Seerr")?.countToday).toBe(3);
    expect(merged.some((item) => item.name === "New")).toBe(true);
  });

  it("lights the tile that was just used and glows a processor while it works", () => {
    withRouter(<ConnectionTiles connections={sortConnections(all)} lit={new Set(["mediaServer:Plex"])} />);
    expect(screen.getByRole("link", { name: "Plex: Media server · 5 sent today" })).toHaveAttribute("data-state", "lit");
    expect(screen.getByRole("link", { name: "Weir: Processor · working on 1" })).toHaveAttribute("data-state", "busy");
    expect(screen.getByRole("link", { name: "Old Plex: Media server · switched off" })).toHaveAttribute("data-state", "off");
    expect(screen.getByRole("link", { name: "Trakt: Lists · watching 2" })).toHaveAttribute("href", "/settings/lists");
  });
});

describe("the right column's layout (#1098)", () => {
  const connection = (kind: string, name: string, countToday: number) => ({ kind, id: name, name, enabled: true, healthy: true, countToday, busy: 0 }) as never;
  it("goes three across at most, and fewer in a narrow column", () => {
    expect(tileColumns(448, 16)).toBe(3);
    expect(tileColumns(352, 16)).toBe(2);
    expect(tileColumns(900, 16)).toBe(MAX_TILE_COLUMNS);
    expect(tileColumns(200, 16)).toBe(1);
  });

  it("draws full tiles while the cards and Activity's least room fit the height, and compact tiles before anything is clipped", () => {
    const counts = [10, 5, 1];
    const cols = 3;
    expect(tileDensity(1300, counts, cols, 16)).toBe("full");
    // A 1920x960 window (an 840px column) holding the rig's 14 indexers, 5 clients and a connection keeps two-line tiles.
    expect(tileDensity(839, [14, 5, 1], cols, 16)).toBe("full");
    expect(tileDensity(615, counts, cols, 16)).toBe("compact");
    // Not measured yet: full.
    expect(tileDensity(0, counts, cols, 16)).toBe("full");
    // Compact tiles take less room than full ones.
    expect(tileCardRem(10, cols, true)).toBeLessThan(tileCardRem(10, cols, false));
  });

  it("draws a compact tile as a light and a name only, with the detail in its name and tooltip", () => {
    render(
      <MemoryRouter>
        <TileLayoutContext.Provider value={{ cols: 3, compact: true }}>
          <ul>
            <Tile to="/x" name="NZBgeek" tone="ok" meta="46 of 100 today" label="NZBgeek: 46 of 100 today" />
          </ul>
        </TileLayoutContext.Provider>
      </MemoryRouter>
    );
    const link = screen.getByRole("link", { name: "NZBgeek: 46 of 100 today" });
    expect(link).toHaveAttribute("title", "NZBgeek: 46 of 100 today");
    expect(within(link).queryByText("46 of 100 today")).toBeNull();
  });

  it("shows the name the tile would be called, not 'Processor on HOST', and does not say the role twice", () => {
    expect(tileName({ name: "Processor on MEDIA-SERVER" })).toBe("Processor");
    expect(tileName({ name: "Plex on NAS" })).toBe("Plex");
    expect(tileName({ name: "My cleaner" })).toBe("My cleaner");
    render(
      <MemoryRouter>
        <ConnectionTiles connections={[connection("processor", "Processor on WIN-PC", 0)]} lit={new Set()} />
      </MemoryRouter>
    );
    const tile = screen.getByRole("link", { name: /^Processor on WIN-PC: Processor · idle/ });
    expect(within(tile).getByText("Processor")).toBeInTheDocument();
    expect(within(tile).getByText("idle")).toBeInTheDocument();
  });

  it("keeps the headers short enough never to be cut", () => {
    expect(indexersSummary([], new Map(), 576)).toBe("0 answering · 576 today");
    expect(connectionsSummary([connection("processor", "Processor on WIN-PC", 0)])).toBe("1 connected");
  });
});
