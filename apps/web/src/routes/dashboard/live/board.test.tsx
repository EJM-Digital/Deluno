import { act, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fullSummary } from "../downloads-fixtures";
import { Band, buildOperations, nextFigure } from "./band";
import { applyConnectionEvents, connectionsSummary, connectionWords, sortConnections } from "./connections";
import { statusOf, type CardContext } from "./card";
import { indexerMeta, indexersSummary } from "./indexers";
import { fitRows } from "./fit-rows";
import { SourcesCard } from "./sources-card";
import { tileHeight, tileLayout } from "./source-tiles";
import { byStage, laneCounts, type JourneyCard, type Stage } from "./journey";
import { tileName } from "./connections";
import { journeyMode, journeySize, MIN_STATION_REM, posterLayout } from "./layout";
import { departedCards, sameTitle } from "./motion";
import { arrivedHeading, Shelf } from "./shelf";
import { SpeedCell } from "./speed-chart";
import { allInBy, buildNext, clockTime, doneAbout, isMediaJob, journeyHeading, usualTimes, whenText } from "./times";
import { NO_TITLES } from "./titles";
import { Journey, stationsFor } from "./track";
import { barFraction, mergeTurnedDown, REASON_ORDER, turnedDownFit } from "./turned-down";
import { TurnedDownCard } from "./turned-down-card";

const status = vi.hoisted(() => ({ value: "connected" }));
const handlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
const emitLive = (name: string, payload: unknown) => handlers.get(name)?.forEach((call) => call(payload));
vi.mock("../../../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  return {
    ...(await importOriginal<typeof import("../../../lib/use-signalr")>()),
    useSignalRStatus: () => status.value,
    useLiveEvent: (name: string, handler: (payload: unknown) => void) => {
      const latest = useRef(handler);
      useEffect(() => { latest.current = handler; });
      useEffect(() => {
        const call = (payload: unknown) => latest.current(payload);
        const set = handlers.get(name) ?? new Set();
        set.add(call);
        handlers.set(name, set);
        return () => { set.delete(call); };
      }, [name]);
    }
  };
});

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
  it("sizes the journey's cards from the lanes' height, the mockup's rule: three rows always, 60px cards at the least, a 130px step at the most", () => {
    expect(journeySize(undefined)).toEqual({ card: 86, step: 94, poster: 69, oneLine: false });
    // (lanes 208 - 22 for the and-more line + 8) / 3 = 64.67, so the least: step 68, card 60, one-line title
    expect(journeySize(208)).toEqual({ card: 60, step: 68, poster: 43, oneLine: true });
    // 292px of lanes (the 398px journey): step floor((292 - 22 + 8) / 3) = 92, card 84
    expect(journeySize(292)).toEqual({ card: 84, step: 92, poster: 67, oneLine: false });
    // 256px: step 80, card 72, a one-line title
    expect(journeySize(256)).toEqual({ card: 72, step: 80, poster: 55, oneLine: true });
    // a tall window: the step stops at 130, a 122px card
    expect(journeySize(900)).toEqual({ card: 122, step: 130, poster: 105, oneLine: false });
    expect(journeySize(40)).toMatchObject({ card: 60, step: 68 });
  });

  it("makes the poster 2:3 at every size: its width is (card - 17) / 1.5", () => {
    for (const room of [208, 256, 292, 380, 900]) {
      const { card, poster } = journeySize(room);
      expect(poster).toBe(card - 17);
    }
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

  it("sizes posters as the mockup does: one row, 2:3, from the shelf's height, between 64px and 130px, more across rather than bigger", () => {
    const short = posterLayout(900, 150);
    const tall = posterLayout(900, 400);
    for (const layout of [short, tall]) {
      expect(layout.rows).toBe(1);
      expect(layout.height).toBe(Math.round(layout.width * 1.5));
      expect(layout.width).toBeGreaterThanOrEqual(40);
      expect(layout.width).toBeLessThanOrEqual(130);
      expect(layout.capacity).toBe(layout.perRow);
    }
    // The height less the 36px caption gives the poster: (150 - 36) * 2/3 = 76.
    expect(short.width).toBe(76);
    expect(tall.width).toBe(130);
    expect(posterLayout(10, 20).width).toBe(40);
    // A wider shelf holds more across, not bigger posters.
    expect(posterLayout(1800, 400).perRow).toBeGreaterThan(posterLayout(900, 400).perRow);
    expect(posterLayout(1800, 400).width).toBe(130);
    expect(posterLayout(0, 0).perRow).toBe(1);
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
    // Only what Deluno does for your media: housekeeping ("Check health") is on System, never here.
    expect(items.map((item) => item.label)).toEqual(["Watch indexers for new releases", "Search Movies", "Lioness S03E08 airs"]);
    expect(items.map((item) => item.kind)).toEqual(["job", "job", "airing"]);
    // An episode that aired hours ago is not next.
    expect(items.some((item) => item.key === "airs:e2")).toBe(false);
  });

  it("never lists housekeeping: only searches, list checks, new-release watching and episodes airing are Deluno's work for your media", () => {
    expect(["search:l1", "task:intake.automation", "task:rss.sync"].every((key) => isMediaJob({ key }))).toBe(true);
    for (const key of ["task:health.checks", "task:dispatch.cleanup", "task:backup.schedule", "task:metadata.refresh", "task:download.state", "task:media.probe", "task:artwork.cache.cleanup"]) expect(isMediaJob({ key })).toBe(false);
    const busy = summary();
    busy.automation!.tasks = ["health.checks", "dispatch.cleanup", "backup.schedule", "intake.automation"].map((key) => ({ key, name: key, lastResult: "completed", lastCompletedUtc: inMs(-60_000), nextRunUtc: inMs(30_000) }));
    expect(buildNext(busy, [], NOW.getTime()).map((item) => item.key)).toEqual(["task:intake.automation", "search:l1"]);
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
  it("counts what is running, lists up to four with a bar where it knows (the rest fit by the card's height), and says how many more are in Activity", () => {
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
    expect(region.querySelector<HTMLElement>(".lb-bar i")?.style.width).toBe("30%");
    // All four are drawn, and all four fit (nothing is measured here, so nothing is hidden): there is nothing more to say.
    expect(within(region).getAllByRole("listitem")).toHaveLength(4);
    expect(within(region).queryByRole("link", { name: /more in Activity/ })).toBeNull();
  });

  it("lists at most four, and says how many more there are", () => {
    const six = Array.from({ length: 6 }, (_, index) => ({ key: `grab:${index}`, kind: "grab" as const, text: `Sending Movie ${index}`, to: "/queue" }));
    withRouter(<Band summary={fullSummary} samples={[]} operations={buildOperations(six, [], NO_TITLES)} upcoming={[]} />);
    const region = screen.getByRole("region", { name: "Working on now" });
    expect(within(region).getAllByRole("listitem")).toHaveLength(4);
    expect(within(region).getByRole("link", { name: "2 more in Activity" })).toHaveAttribute("href", "/activity");
  });

  it("shows only the rows that fit its height, hiding the rest and counting them", () => {
    const host = document.createElement("div");
    host.innerHTML = '<div data-fit></div><div data-fit></div><div data-fit></div>';
    const rows = Array.from(host.children) as HTMLElement[];
    host.getBoundingClientRect = () => ({ bottom: 100 }) as DOMRect;
    // rows end at 40, 80 and 120: the third runs past the card
    rows.forEach((row, index) => { row.getBoundingClientRect = () => ({ bottom: 40 * (index + 1) }) as DOMRect; });
    expect(fitRows(host)).toBe(2);
    expect(rows.map((row) => row.style.visibility)).toEqual(["", "", "hidden"]);
    // A row's own bottom padding may run past the card; its content may not
    const padded = document.createElement("div");
    padded.innerHTML = '<div data-fit style="padding-bottom: 7px"></div>';
    padded.getBoundingClientRect = () => ({ bottom: 100 }) as DOMRect;
    (padded.firstElementChild as HTMLElement).getBoundingClientRect = () => ({ bottom: 105 }) as DOMRect;
    expect(fitRows(padded)).toBe(1);
    // A card that grows with its rows shows them all
    expect(fitRows(host, false)).toBe(3);
    expect(rows.map((row) => row.style.visibility)).toEqual(["", "", ""]);
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
    // The ribbon segment keeps to its icon, name and count; how long it usually takes is on hover.
    expect(screen.getByRole("link", { name: "Downloading: 0" })).toHaveAttribute("title", "Downloading: 0 · usually 12 min");
    expect(screen.getByRole("link", { name: "Grabbed: 0" })).not.toHaveAttribute("title");
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
    const downloading = screen.getByRole("link", { name: "Stuck One (2001): Stuck at 43% · not moving" });
    expect(downloading).toHaveAttribute("data-glide-stage", "downloading");
    expect(screen.getByText("done about 2:14 pm")).toBeInTheDocument();
    expect(screen.getByText("You asked")).toBeInTheDocument();
    // A card that came from Seerr says so; a request card says it in its line and not twice.
    const requested = document.querySelector<HTMLElement>('[data-glide-stage="requested"]')!;
    expect(requested).toHaveAccessibleName("Shogun: Asked for in Seerr · looking");
    expect(within(requested).queryByText("Seerr")).toBeNull();
    // A card with no recorded reason has no tag rather than a guess.
    expect(within(document.querySelector<HTMLElement>('[data-glide-stage="processor"]')!).queryByText(/You asked|Seerr|From a list|Upgrade hunt|Missing episode/)).toBeNull();
    expect(screen.getByRole("link", { name: /Stuck One/ }).querySelector(".lj-pulse")).not.toBeNull();
  });

  it("tags a download that came from Seerr, a list, an upgrade hunt or a missing episode", () => {
    render5([card({ key: "a", title: "A (2001)", startedBy: "seerr" }), card({ key: "b", title: "B (2002)", startedBy: "list" }), card({ key: "c", title: "C (2003)", startedBy: "upgrade" }), card({ key: "d", title: "D (2004)", startedBy: "missing" })]);
    for (const words of ["Seerr", "From a list", "Upgrade hunt", "Missing episode"]) expect(screen.getByText(words)).toBeInTheDocument();
  });

  it("shows a title whose import has finished full and green: Imported, in your library", () => {
    render5([card({ key: "a", stage: "importing", title: "Done (2001)", landed: true, progress: 100, state: { tone: "ok", label: "Imported" } })]);
    const token = screen.getByRole("link", { name: "Done (2001): ✓ Imported · in your library" });
    expect(token).toHaveClass("lj-done");
    expect(token.querySelector(".lj-pb i")).toHaveStyle({ width: "100%" });
    expect(token.querySelector(".lj-wait")).toBeNull();
  });

  it("draws the ribbon: one segment per station with its icon, name and count, the busy ones lit and the empty ones dimmed", () => {
    render5(cards);
    const requested = screen.getByRole("group", { name: "Requested" });
    expect(requested).toHaveClass("lj-station", "lj-active");
    expect(requested.textContent).toContain("Requested");
    expect(requested.textContent).toContain("1 here");
    render5([]);
    expect(screen.getAllByRole("group", { name: "Importing" }).at(-1)).toHaveClass("lj-empty");
  });

  it("an import that is under way moves its bar without claiming how far it is", () => {
    render5([card({ key: "a", stage: "importing", title: "Busy (2001)", state: { tone: "info", label: "Importing now" } })]);
    expect(screen.getByRole("link", { name: /^Busy \(2001\)/ }).querySelector(".lj-pb")).toHaveClass("lj-wait");
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

  it("shows as many cards as fit a station and ends it with an and-N-more link to Transfers that names every title", () => {
    const rect = HTMLElement.prototype.getBoundingClientRect;
    const observer = globalThis.ResizeObserver;
    globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
    HTMLElement.prototype.getBoundingClientRect = function sized() {
      const make = (width: number, height: number) => ({ width, height, top: 0, left: 0, right: width, bottom: height, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
      return typeof this.className === "string" && this.className.includes("lj ") ? make(1200, 400) : make(0, 0);
    };
    try {
      const many = Array.from({ length: 12 }, (_, n) => card({ key: `m${n}`, title: `Film ${n} (2001)` }));
      const grouped = byStage(many);
      withRouter(<Journey budget={400} grouped={grouped} counts={laneCounts(grouped, undefined)} context={context} hasProcessor usual={{}} by={undefined} lastArrival={null} />);
      const more = screen.getByRole("link", { name: /^and [0-9]+ more/ });
      expect(more).toHaveAttribute("href", "/queue");
      expect(more.getAttribute("title")).toContain("Film 11 (2001)");
      // Every card is one size and the ones that do not fit are kept out of sight, not removed.
      expect(document.querySelectorAll(".lj-token").length).toBe(12);
      expect(document.querySelectorAll(".lj-hidden").length).toBeGreaterThan(0);
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
      if (name.includes("lj ")) return make(360, 600);
      return make(0, 0);
    };
    try {
      render5(cards);
      // Each station's heading is followed by its own cards, and a station with nothing is left out.
      const stacked = Array.from(document.querySelectorAll('[data-station]'));
      expect(stacked.map((element) => element.getAttribute("data-station"))).toEqual(["requested", "downloading", "processor"]);
      expect(within(stacked[1] as HTMLElement).getByRole("group", { name: "Downloading" })).toBeInTheDocument();
    } finally {
      HTMLElement.prototype.getBoundingClientRect = rect;
      globalThis.ResizeObserver = observer;
    }
  });

  it("only shimmers a download bar while bytes are really moving", () => {
    const { container, unmount } = render5([card({ key: "a" })]);
    expect(container.querySelector(".lj-moving")).not.toBeNull();
    unmount();
    const idle = render5([card({ key: "a", speedMbps: 0 })]);
    expect(idle.container.querySelector(".lj-moving")).toBeNull();
  });

  it("glows a card that has just changed station, and does not when motion is reduced", () => {
    const grouped = byStage([card({ key: "a" })]);
    const draw = () => withRouter(<Journey grouped={grouped} counts={laneCounts(grouped, undefined)} context={{ ...context, arrivals: new Set(["a"]) }} hasProcessor usual={{}} by={undefined} lastArrival={null} />);
    expect(draw().container.querySelector(".lj-arrive")).not.toBeNull();
    setReducedMotion(true);
    expect(draw().container.querySelector(".lj-arrive")).toBeNull();
  });
});

describe("flying into Arrived today", () => {
  it("finds every card that left the board, from any station, and not those that only moved on", () => {
    // An import can finish between two readings, so a card may leave straight from Downloading; only one whose title arrives flies.
    const before = new Map<string, Stage>([["a", "importing"], ["b", "importing"], ["c", "downloading"], ["d", "grabbed"]]);
    const now = [card({ key: "b", stage: "importing" }), card({ key: "d", stage: "downloading" })];
    expect(departedCards(before, now)).toEqual(["a", "c"]);
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
    expect(first.closest("li")).toHaveAttribute("data-new");
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
    expect((card.querySelector('[data-reason="language"] span[aria-hidden] i') as HTMLElement).style.width).toBe("100%");
    expect((card.querySelector('[data-reason="notBetter"] span[aria-hidden] i') as HTMLElement).style.width).toBe("79%");
    expect(within(card).getByRole("link", { name: /Dune.2024.iTA.1080p/ })).toHaveAttribute("href", "/movies/m1");
  });

  it("opens Activity on today's turned-down releases, and a reason's bar opens just that reason (#1100)", () => {
    withRouter(<TurnedDownCard view={mergeTurnedDown(base, [], inMs(0))} lit={new Set()} />);
    const card = screen.getByRole("region", { name: "Turned down today" });

    expect(within(card).getByRole("link", { name: /See them all/ })).toHaveAttribute("href", "/activity?type=turned-down&since=today");
    expect(within(card).getByRole("link", { name: /Not in a language you want: 14/ })).toHaveAttribute("href", "/activity?type=turned-down&since=today&reason=language");
    expect(within(card).getByRole("link", { name: /Bigger than your size limit: 9/ })).toHaveAttribute("href", "/activity?type=turned-down&since=today&reason=tooBig");
  });

  it("draws only the whole rows the height holds: fewer bars on a short card, the newest releases only where there is room", () => {
    expect(turnedDownFit(0, 16, 6, 20)).toEqual({ reasons: 6, latest: 20 });
    expect(turnedDownFit(140, 16, 6, 20)).toEqual({ reasons: 2, latest: 0 });
    expect(turnedDownFit(300, 16, 6, 0)).toEqual({ reasons: 6, latest: 0 });
    expect(turnedDownFit(300, 16, 6, 20)).toEqual({ reasons: 5, latest: 2 });
    const tall = turnedDownFit(520, 16, 6, 20);
    expect(tall.reasons).toBe(5);
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

  it("adds only releases new today from a pushed search, so a repeat search never counts a release twice", () => {
    const pushed = [
      { atUtc: inMs(5_000), title: "Moon", entityType: "movie", entityId: "m2", items: [
        { release: "Moon.FRENCH", group: "language", label: "Not in a language you want", newToday: false },
        { release: "Moon.CAM", group: "fake", label: "Fake or mislabelled", newToday: true }
      ] }
    ] as never;
    const view = mergeTurnedDown(base, pushed, inMs(0));
    expect(view.total).toBe(44);
    expect(view.reasons.find((reason) => reason.group === "language")?.count).toBe(14);
    expect(view.reasons.find((reason) => reason.group === "fake")?.count).toBe(5);
    expect(view.latest[0]).toMatchObject({ release: "Moon.CAM" });
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

describe("the Sources card", () => {
  const indexer = (id: string, extra: Record<string, unknown> = {}) => ({ id, name: id, isEnabled: true, priority: 0, healthStatus: "healthy", lastHealthLatencyMs: 320, rateLimitedUntilUtc: null, ...extra }) as never;
  const board = { indexers: [{ id: "NZBgeek", name: "NZBgeek", totalQueries: 46, failedQueries: 0, successfulGrabs: 1, lastQueryUtc: null }, { id: "Nyaa", name: "Nyaa", totalQueries: 5, failedQueries: 5, successfulGrabs: 0, lastQueryUtc: null }] };
  const list = [indexer("Zeta"), indexer("NZBgeek", { queryLimit: 100, limitsUnit: "day", queryLimitStatus: { allowed: true, limit: 100, count: 46, resetsAtUtc: null, unit: "day" } }), indexer("Nyaa", { healthStatus: "unreachable" }), indexer("Old", { isEnabled: false })];
  const row = (id: string, extra: Record<string, unknown> = {}) => ({ id, name: id, protocol: "torrent", reachable: true, paused: false, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "healthy", active: 0, queued: 0, ...extra }) as never;
  const connection = (kind: string, name: string, countToday: number, extra: object = {}) => ({ kind, id: name, name, enabled: true, healthy: true, countToday, busy: 0, ...extra }) as never;
  const all = [connection("lists", "Trakt", 2), connection("processor", "Weir", 6, { busy: 1 }), connection("subtitles", "OpenSubtitles", 12), connection("requests", "Seerr", 3), connection("mediaServer", "Plex", 5), connection("processor", "FileFlows", 0), connection("mediaServer", "Old Plex", 0, { enabled: false })];

  function draw() {
    return withRouter(
      <SourcesCard
        indexers={list}
        scoreboard={board}
        summary={undefined}
        now={NOW.getTime()}
        clientRows={[row("qBittorrent"), row("SABnzbd", { reachable: false })]}
        clients={[{ id: "qBittorrent", name: "qBittorrent", isEnabled: true }, { id: "SABnzbd", name: "SABnzbd", isEnabled: true }] as never}
        speeds={new Map([["qBittorrent", 6.2]])}
        seeding={new Map([["qBittorrent", 8], ["SABnzbd", 0]])}
        connections={sortConnections(all)}
      />
    );
  }

  it("draws every indexer, client and connection as a link with its own name, and where a click goes", () => {
    draw();
    expect(screen.getByRole("link", { name: /^NZBgeek: 46 of 100 today · 320 ms/ })).toHaveAttribute("href", "/indexers/indexers");
    expect(screen.getByRole("link", { name: /^Nyaa: not answering · 5 today/ })).toHaveAttribute("href", "/?tab=system");
    expect(screen.getByRole("link", { name: /^Old: switched off/ })).toHaveAttribute("data-off");
    expect(screen.getByRole("link", { name: "qBittorrent: moving 6.2 MB/s, seeding 8" })).toHaveAttribute("href", "/indexers/download-clients");
    expect(screen.getByRole("link", { name: "SABnzbd: not answering" })).toHaveAttribute("href", "/?tab=system");
    expect(screen.getByRole("link", { name: "Weir: Processor · working on 1" })).toHaveAttribute("href", "/indexers/companion-tools");
    expect(screen.getByRole("link", { name: "Trakt: Lists · watching 2" })).toHaveAttribute("href", "/settings/lists");
    const names = screen.getAllByRole("link").map((link) => link.getAttribute("aria-label")).filter(Boolean);
    expect(new Set(names).size).toBe(names.length);
  });

  it("colours each by its status, with a switched-off one grey and last in its group", () => {
    draw();
    expect(screen.getByRole("link", { name: /^NZBgeek/ })).toHaveAttribute("data-tone", "ok");
    expect(screen.getByRole("link", { name: /^Nyaa/ })).toHaveClass("down");
    expect(screen.getByRole("link", { name: /^SABnzbd/ })).toHaveClass("down");
    expect(screen.getByRole("link", { name: /^Old:/ })).toHaveClass("off");
    expect(screen.getByRole("link", { name: /^qBittorrent/ })).toHaveClass("moving");
    expect(screen.getByRole("link", { name: /^Weir/ })).toHaveClass("busy");
    const order = screen.getAllByRole("link").map((link) => link.getAttribute("aria-label")?.split(":")[0]);
    expect(order.indexOf("Old")).toBeGreaterThan(order.indexOf("Zeta"));
  });

  it("heads each group with its name and counts, and says each tile's small words", () => {
    draw();
    expect(screen.getByText("Indexers")).toBeInTheDocument();
    expect(screen.getByText("Download clients")).toBeInTheDocument();
    expect(screen.getByText("Connections")).toBeInTheDocument();
    expect(screen.getByText("2 of 3 answering · 1 off · 51 today")).toBeInTheDocument();
    expect(screen.getByText("1 moving · 8 seeding")).toBeInTheDocument();
    expect(screen.getByText("6 connected · all answering · 1 off")).toBeInTheDocument();
    expect(screen.getByText("46 of 100 today · 320 ms")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /^qBittorrent/ })).toHaveTextContent("6.2 MB/s · 8 seeding");
    expect(screen.getByText("6.2").tagName).toBe("B");
    expect(screen.getByText("Processor · working on 1")).toBeInTheDocument();
    expect(screen.getByText("switched off")).toBeInTheDocument();
  });

  it("flashes a tile when Deluno asks an indexer, and again when it answers", async () => {
    draw();
    const nzbgeek = screen.getByRole("link", { name: /^NZBgeek/ });
    await act(async () => { emitLive("IndexerQueryRecorded", { indexerId: "NZBgeek", phase: "asked" }); });
    expect(nzbgeek).toHaveClass("asked");
    await act(async () => { emitLive("IndexerQueryRecorded", { indexerId: "NZBgeek", phase: "answered", resultCount: 3, elapsedMs: 300, failed: false, failure: null }); });
    await waitFor(() => expect(nzbgeek).toHaveClass("answered"));
    expect(nzbgeek).not.toHaveClass("asked");
    await act(async () => { emitLive("IndexerQueryRecorded", { indexerId: "Zeta", phase: "answered", resultCount: null, elapsedMs: 0, failed: true, failure: "timeout" }); });
    await waitFor(() => expect(screen.getByRole("link", { name: /^Zeta/ })).toHaveClass("failed"));
  });

  it("sizes every tile from the card, one height for all three groups, down to one line when short", () => {
    const counts = [12, 3, 6] as const;
    expect(tileLayout(520, 900, counts)).toEqual({ columns: 3, tile: 54 });
    expect(tileLayout(520, 520, counts).tile).toBeLessThan(54);
    expect(tileLayout(520, 0, counts).tile).toBe(44);
    // A narrow card puts two across, and three only when two would not fit even as one-line tiles.
    expect(tileLayout(380, 700, counts).columns).toBe(2);
    expect(tileLayout(352, 383, counts)).toEqual({ columns: 3, tile: 32 });
    expect(tileHeight(100, 3, counts)).toBe(30);
  });

  it("says what a connection did today in plain words", () => {
    expect(connectionWords(connection("processor", "Weir", 6, { busy: 1 }))).toBe("Processor · working on 1");
    expect(connectionWords(connection("processor", "FileFlows", 0))).toBe("Processor · idle");
    expect(connectionWords(connection("mediaServer", "Plex", 5))).toBe("Media server · 5 sent today");
    expect(connectionWords(connection("requests", "Seerr", 3))).toBe("Requests · 3 today");
    expect(connectionWords(connection("subtitles", "OpenSubtitles", 12))).toBe("Subtitles · 12 fetched today");
    expect(connectionWords(connection("lists", "Trakt", 2))).toBe("Lists · watching 2");
    expect(connectionWords(connection("lists", "Trakt", 2, { enabled: false }))).toBe("Lists · switched off");
    expect(connectionWords(connection("lists", "Trakt", 2, { healthy: false }))).toBe("Lists · not answering");
  });

  it("counts answering, switched off and the queries today", () => {
    const today = new Map(board.indexers.map((item) => [item.id, item]));
    expect(indexersSummary(list, today as never)).toBe("2 of 3 answering · 1 off · 51 today");
    expect(indexerMeta(indexer("x", { rateLimitedUntilUtc: new Date(NOW.getTime() + 60_000).toISOString() }), undefined, NOW.getTime()).text).toMatch(/^asked too often, waiting/);
    expect(indexerMeta(indexer("x", { lastHealthLatencyMs: 2500 }), undefined, NOW.getTime()).tone).toBe("warn");
    // An hourly limit is not a count of today, so only the day's queries are said.
    expect(indexerMeta(indexer("x", { queryLimit: 20, limitsUnit: "hour" }), { totalQueries: 7 } as never, NOW.getTime()).text).toBe("7 today · 320 ms");
    expect(indexersSummary([], new Map(), 576)).toBe("0 of 0 answering · 576 today");
  });

  it("orders connections by role with the switched-off ones last, and counts them", () => {
    expect(sortConnections(all).map((item) => item.name)).toEqual(["FileFlows", "Weir", "Plex", "Seerr", "OpenSubtitles", "Trakt", "Old Plex"]);
    expect(connectionsSummary(all)).toBe("6 connected · all answering · 1 off");
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

  it("shows the name the connection would be called, not 'Processor on HOST'", () => {
    expect(tileName({ name: "Processor on MEDIA-SERVER" })).toBe("Processor");
    expect(tileName({ name: "Plex on NAS" })).toBe("Plex");
    expect(tileName({ name: "My cleaner" })).toBe("My cleaner");
  });
});
