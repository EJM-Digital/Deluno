import { act, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fullSummary } from "../downloads-fixtures";
import { buildCountdownRows, Countdowns } from "./countdowns";
import { Flow } from "./flow";
import { HeartbeatValue } from "./heartbeat";
import { byStage, laneCounts, type JourneyCard } from "./journey";
import { Indexers } from "./indexers";
import { Rhythm } from "./rhythm";
import { SpeedChart } from "./speed-chart";

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
  const card = (overrides: Partial<JourneyCard>): JourneyCard => ({ key: "c1:a", stage: "downloading", title: "Sintel (2010)", mediaType: "movie", state: { tone: "info", label: "Downloading" }, progress: 40, speedMbps: 2, etaSeconds: 600, sizeBytes: 2e9, quality: "Bluray 1080p", indexer: "NZBgeek", client: "Deluge", to: "/queue", ...overrides });
  const board = (cards: JourneyCard[], arrivals: string[] = []) => {
    const grouped = byStage(cards);
    return (
      <MemoryRouter>
        <Flow grouped={grouped} counts={laneCounts(grouped, undefined)} summary={undefined} posters={new Map()} arrivals={new Set(arrivals)} action={{ busy: null, run: async () => {} }} stalledWithButton={new Set()} />
      </MemoryRouter>
    );
  };

  it("puts each title in its lane with its details, and an empty lane says something true", () => {
    render(board([card({}), card({ key: "c1:b", stage: "processor", title: "Metropolis (1927)", processor: "Cleaner", state: { tone: "info", label: "Being cleaned" } })]));
    const downloading = screen.getByRole("group", { name: "Downloading" });
    expect(within(downloading).getByText("Sintel (2010)")).toBeInTheDocument();
    expect(within(downloading).getByText("Bluray 1080p · 1.9 GB")).toBeInTheDocument();
    expect(within(downloading).getByText("NZBgeek → Deluge")).toBeInTheDocument();
    expect(within(screen.getByRole("group", { name: "With the processor" })).getByText("NZBgeek → Deluge → Cleaner")).toBeInTheDocument();
    expect(within(screen.getByRole("group", { name: "Importing" })).getByText("Nothing waiting to import.")).toBeInTheDocument();
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
