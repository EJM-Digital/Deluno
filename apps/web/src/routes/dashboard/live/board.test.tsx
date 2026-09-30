import { act, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fullSummary } from "../downloads-fixtures";
import { buildCountdownRows, Countdowns } from "./countdowns";
import { Flow } from "./flow";
import { HeartbeatValue } from "./heartbeat";
import { byStage, laneCounts, type JourneyCard } from "./journey";
import { NO_TITLES, type TitleLookup } from "./titles";
import { Indexers } from "./indexers";
import { Rhythm } from "./rhythm";
import { SpeedChart } from "./speed-chart";
import { ActivityStream } from "./stream";

const status = vi.hoisted(() => ({ value: "connected" }));
vi.mock("../../../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../../lib/use-signalr")>()),
  useSignalRStatus: () => status.value
}));

const NOW = new Date("2026-10-01T10:00:00Z");
const inMs = (ms: number) => new Date(NOW.getTime() + ms).toISOString();

function withRouter(node: React.ReactNode) {
  return render(<MemoryRouter>{node}</MemoryRouter>);
}

function setReducedMotion(reduce: boolean) {
  window.matchMedia = ((query: string) => ({ matches: reduce && query.includes("prefers-reduced-motion"), media: query, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false, onchange: null })) as typeof window.matchMedia;
}

beforeEach(() => {
  status.value = "connected";
  setReducedMotion(false);
});
afterEach(() => vi.useRealTimers());

describe("the countdowns", () => {
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

  it("lists the searches first, then the named tasks, and leaves out what is not scheduled", () => {
    expect(buildCountdownRows(summary()).map((row) => row.label)).toEqual(["Search Movies", "Check health", "Watch indexers for new releases"]);
  });

  it("counts down by the second, and the next search reads 12:04", () => {
    vi.useFakeTimers({ now: NOW });
    withRouter(<Countdowns summary={summary()} />);
    const search = screen.getByRole("link", { name: /^Search Movies/ });
    expect(within(search).getByText("12:04")).toBeInTheDocument();
    act(() => { vi.advanceTimersByTime(3_000); });
    expect(within(screen.getByRole("link", { name: /^Search Movies/ })).getByText("12:01")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /^Search Movies/ })).toHaveAttribute("href", "/search-cycles");
  });

  it("says something true when nothing is scheduled", () => {
    withRouter(<Countdowns summary={undefined} />);
    expect(screen.getByText(/Nothing is scheduled/)).toBeInTheDocument();
  });
});

describe("the heartbeat", () => {
  it("pulses while connected and says so in words when it is not", () => {
    const { container, rerender } = render(<HeartbeatValue />);
    expect(screen.getByText("Live")).toBeInTheDocument();
    expect(container.querySelector(".live-beat")).not.toBeNull();
    for (const [state, words] of [["reconnecting", "Reconnecting"], ["disconnected", "Offline"]] as const) {
      status.value = state;
      rerender(<HeartbeatValue />);
      expect(screen.getByText(words)).toBeInTheDocument();
      expect(container.querySelector(".live-beat")).toBeNull();
    }
  });

  it("does not pulse when motion is reduced, and the word stays", () => {
    setReducedMotion(true);
    const { container } = render(<HeartbeatValue />);
    expect(screen.getByText("Live")).toBeInTheDocument();
    expect(container.querySelector(".live-beat")).toBeNull();
  });
});

describe("the charts", () => {
  it("draws the speed trace with a reading even when nothing has ever been stored", () => {
    vi.useFakeTimers({ now: NOW });
    const { container } = withRouter(<SpeedChart samples={[]} summary={undefined} />);
    expect(container.querySelectorAll("path.live-trace").length).toBe(2);
    expect(screen.getByRole("img", { name: /Download peaked at 0 MB\/s/ })).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/NaN|undefined/);
  });

  it("extends the trace with the newest reading from the summary", () => {
    vi.useFakeTimers({ now: NOW });
    const summary = structuredClone(fullSummary);
    summary.downloads!.readingUtc = inMs(-30_000);
    summary.downloads!.downloadMbps = 12;
    withRouter(<SpeedChart samples={[{ capturedUtc: inMs(-3_600_000), speedMbps: 4, activeCount: 1, uploadMbps: 0 }]} summary={summary} />);
    expect(screen.getByRole("img", { name: /Download peaked at 12 MB\/s/ })).toBeInTheDocument();
  });

  it("shows today's hours as bars from stored activity, growing with new events, and says so when quiet", () => {
    vi.useFakeTimers({ now: NOW });
    const at = (hour: number) => new Date(new Date(NOW).setHours(hour, 10, 0, 0)).toISOString();
    const rows = [
      { id: "1", category: "wanted.search.completed", createdUtc: at(2) },
      { id: "2", category: "filesystem.import.completed", createdUtc: at(2) }
    ];
    const page = (list: typeof rows) => <MemoryRouter><Rhythm rows={list} /></MemoryRouter>;
    const { container, rerender } = render(page(rows));
    expect(container.querySelectorAll(".live-bar")).toHaveLength(2);
    rerender(page([...rows, { id: "3", category: "filesystem.import.completed", createdUtc: at(2) }]));
    expect(container.querySelectorAll(".live-bar")).toHaveLength(2);
    expect(screen.getByRole("img", { name: /3 events today: 1 searches, 2 imports/ })).toBeInTheDocument();
    rerender(page([]));
    expect(screen.getByText(/Quiet so far today/)).toBeInTheDocument();
  });
});

describe("today's count", () => {
  it("shows the day's real total from the server counts, never the size of a page", () => {
    vi.useFakeTimers({ now: NOW });
    const counts = [{ hour: 3, category: "wanted.search.completed", count: 340 }, { hour: 5, category: "movies.release.grabbed", count: 60 }];
    const { container } = withRouter(<Rhythm rows={[]} counts={counts} countedAt={NOW.getTime()} />);
    expect(screen.getByRole("img", { name: /400 events today/ })).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/last 200/);
  });

  it("says so when it only has the newest page, and does not present the cap as the total", () => {
    vi.useFakeTimers({ now: NOW });
    const rows = [{ id: "1", category: "wanted.search.completed", createdUtc: inMs(-3_600_000) }];
    withRouter(<Rhythm rows={rows} capped />);
    expect(screen.getByText("last 200 events")).toBeInTheDocument();
  });
});

describe("the speed peaks", () => {
  it("labels the download peak and the upload peak separately", () => {
    vi.useFakeTimers({ now: NOW });
    const summary = structuredClone(fullSummary);
    summary.downloads!.readingUtc = inMs(-30_000);
    summary.downloads!.downloadMbps = 12;
    summary.downloads!.uploadMbps = 3;
    withRouter(<SpeedChart samples={[]} summary={summary} />);
    expect(screen.getByText(/^Download peak/).textContent).toContain("12 MB/s");
    expect(screen.getByText(/^Upload peak/).textContent).toContain("3.0 MB/s");
  });
});

describe("the indexers", () => {
  const indexer = (id: string, name: string, healthStatus: string) => ({ id, name, protocol: "usenet", isEnabled: true, priority: 1, healthStatus, lastHealthMessage: healthStatus === "healthy" ? null : "HTTP 504", lastHealthLatencyMs: 200, lastHealthTestUtc: inMs(-60_000), consecutiveFailures: 0, rateLimitedUntilUtc: null }) as never;

  it("leaves a failing indexer to Needs you, which has its button, so it is never listed twice", () => {
    vi.useFakeTimers({ now: NOW });
    withRouter(<Indexers indexers={[indexer("a", "NZBgeek", "healthy"), indexer("b", "Nyaa.si", "unreachable")]} summary={undefined} now={NOW.getTime()} />);
    expect(screen.getByText("NZBgeek")).toBeInTheDocument();
    expect(screen.queryByText("Nyaa.si")).toBeNull();
  });
});

describe("the journey board", () => {
  const card = (overrides: Partial<JourneyCard>): JourneyCard => ({ key: "c1:a", stage: "downloading", title: "Sintel 2010 1080p", release: "Sintel.2010.1080p.BluRay", state: { tone: "info", label: "Downloading" }, progress: 40, speedMbps: 2, etaSeconds: 600, sizeBytes: 2e9, quality: "Bluray 1080p", indexer: "NZBgeek", client: "Deluge", to: "/queue", ...overrides });
  const named: TitleLookup = {
    movies: new Map([["m1", { title: "Sintel", year: 2010, posterUrl: "data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg'/>" }]]),
    series: new Map([["s1", { title: "Say Yes to the Dress", year: 2007, posterUrl: null }]]),
    episodes: new Map([["e1", { seriesId: "s1", season: 4, episode: 2 }]])
  };
  const board = (cards: JourneyCard[], arrivals: string[] = [], titles: TitleLookup = NO_TITLES) => {
    const grouped = byStage(cards);
    return (
      <MemoryRouter>
        <Flow grouped={grouped} counts={laneCounts(grouped, undefined)} summary={undefined} titles={titles} arrivals={new Set(arrivals)} action={{ busy: null, run: async () => {} }} />
      </MemoryRouter>
    );
  };

  it("puts each title in its lane with its details, and an empty lane shrinks to one true line", () => {
    render(board([card({}), card({ key: "c1:b", stage: "processor", title: "Metropolis (1927)", processor: "Cleaner", state: { tone: "info", label: "Being cleaned" } })]));
    const downloading = screen.getByRole("group", { name: "Downloading" });
    expect(within(downloading).getByText("Sintel 2010 1080p")).toBeInTheDocument();
    expect(within(downloading).getByText("Bluray 1080p · 1.9 GB")).toBeInTheDocument();
    expect(within(downloading).getByText("NZBgeek → Deluge")).toBeInTheDocument();
    expect(within(screen.getByRole("group", { name: "With the processor" })).getByText("NZBgeek → Deluge → Cleaner")).toBeInTheDocument();
    const importing = screen.getByRole("group", { name: "Importing" });
    expect(within(importing).getByText("Nothing waiting")).toBeInTheDocument();
    expect(importing.className).toContain("lg:w-[6.75rem]");
    expect(downloading.className).not.toContain("lg:w-[6.75rem]");
  });

  it("names a card by its library title with the release as a quiet second line, and shows an episode as show plus SxxEyy", () => {
    render(board([
      card({ entity: { kind: "movie", id: "m1" } }),
      card({ key: "c1:b", title: "Say Yes 2007", release: "Say.Yes.to.the.Dress.2007.S04E02.720p", entity: { kind: "episode", id: "e1" }, progress: 10 }),
      card({ key: "c1:c", title: "Nobody 2020", release: "Nobody.2020.1080p", entity: { kind: "movie", id: "gone" } })
    ], [], named));
    const downloading = screen.getByRole("group", { name: "Downloading" });
    expect(within(downloading).getByText("Sintel (2010)")).toBeInTheDocument();
    expect(within(downloading).getByText("Sintel.2010.1080p.BluRay")).toBeInTheDocument();
    expect(within(downloading).getByText("Say Yes to the Dress S04E02")).toBeInTheDocument();
    // A title that no longer exists keeps the release's own name and gets no failing request.
    expect(within(downloading).getByText("Nobody 2020")).toBeInTheDocument();
    expect(within(downloading).getByRole("link", { name: /^Sintel \(2010\)/ })).toHaveAttribute("href", "/movies/m1");
    expect(within(downloading).getByRole("link", { name: /^Say Yes to the Dress S04E02/ })).toHaveAttribute("href", "/tv/s1");
  });

  it("puts a stopped download in Stuck with its reason and Search again, and keeps it out of Downloading", () => {
    render(board([
      card({ key: "c1:s", stage: "stuck", state: { tone: "bad", label: "Stuck" }, reason: "missing or invalid torrent data", canSearchAgain: true, queueRef: { clientId: "c1", itemId: "s" }, progress: 100, speedMbps: 0 }),
      card({})
    ]));
    const stuck = screen.getByRole("group", { name: "Stuck" });
    expect(within(stuck).getByText("missing or invalid torrent data")).toBeInTheDocument();
    expect(within(stuck).getByRole("button", { name: /^Search again for/ })).toBeInTheDocument();
    expect(within(screen.getByRole("group", { name: "Downloading" })).queryByText("Stuck")).toBeNull();
  });

  it("gives a failed processor hand-off its Retry and Give up on the card", () => {
    render(board([card({ key: "h:1", stage: "processor", title: "Metropolis (1927)", processor: "Cleaner", state: { tone: "bad", label: "Processor failed" }, reason: "The processor timed out.", failedHandoffId: "h1" })]));
    const lane = screen.getByRole("group", { name: "With the processor" });
    expect(within(lane).getByRole("button", { name: "Retry Metropolis (1927)" })).toBeInTheDocument();
    expect(within(lane).getByRole("button", { name: "Give up on Metropolis (1927)" })).toBeInTheDocument();
  });

  it("only shimmers a download bar while bytes are really moving", () => {
    const { container, unmount } = render(board([card({ speedMbps: 3 })]));
    expect(container.querySelector(".live-shimmer")).not.toBeNull();
    unmount();
    const idle = render(board([card({ speedMbps: 0 })]));
    expect(idle.container.querySelector(".live-shimmer")).toBeNull();
  });

  it("glows a card that has just arrived, and does not when motion is reduced", () => {
    const { container, unmount } = render(board([card({})], ["c1:a"]));
    expect(container.querySelector(".live-card-arrive")).not.toBeNull();
    unmount();
    setReducedMotion(true);
    const reduced = render(board([card({})], ["c1:a"]));
    expect(reduced.container.querySelector(".live-card-arrive")).toBeNull();
    // The numbers stay.
    expect(within(reduced.container).getAllByText("40%").length).toBeGreaterThan(0);
  });
});

describe("what Today and Latest show", () => {
  const row = (id: string, category: string, message: string, createdUtc = inMs(-60_000)) => ({ id, category, message, createdUtc, fresh: false });

  it("leaves routine background work out of Latest and says so once, quietly", () => {
    withRouter(
      <ActivityStream
        rows={[
          row("1", "movies.release.grabbed", "Grabbed Dune Part Two (2024) from NZBgeek."),
          row("2", "job.started", "Task started"),
          row("3", "job.completed", "Task finished"),
          row("4", "metadata.artwork.cleanup", "Artwork cache cleanup found nothing to remove.")
        ]}
      />
    );
    expect(screen.getByText(/Grabbed Dune Part Two/)).toBeInTheDocument();
    expect(screen.queryByText("Task started")).toBeNull();
    expect(screen.queryByText(/Artwork cache/)).toBeNull();
    expect(screen.getByText(/and 3 routine background events, left out/)).toBeInTheDocument();
  });

  it("names each colour of Today in words and keeps routine work out of the bars", () => {
    vi.useFakeTimers({ now: NOW });
    const counts = [
      { hour: 3, category: "wanted.search.completed", count: 135 },
      { hour: 4, category: "movies.release.grabbed", count: 2 },
      { hour: 5, category: "filesystem.import.completed", count: 6 },
      { hour: 6, category: "job.completed", count: 471 }
    ];
    const { container } = withRouter(<Rhythm rows={[]} counts={counts} countedAt={NOW.getTime()} />);
    expect(screen.getByRole("img", { name: /143 events today/ })).toBeInTheDocument();
    for (const words of ["searches", "grabbed", "imported", "routine"]) expect(screen.getByText(words)).toBeInTheDocument();
    expect(container.querySelectorAll(".live-bar")).toHaveLength(3);
  });
});
