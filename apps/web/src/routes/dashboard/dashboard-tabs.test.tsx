import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../../lib/api";
import { fullSummary, emptySummary } from "./summary-fixtures";

const api = vi.hoisted(() => ({ fetchJson: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchJson: api.fetchJson
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve(new Response("{}"))) }));

const { queryClient } = await import("../../lib/route-cache");
const { OverviewTab, buildAreaLines } = await import("./overview-tab");
const { LibraryTab } = await import("./library-tab");
const { computeAttention } = await import("./attention");

const wanted = {
  totalWanted: 3,
  missingCount: 2,
  upgradeCount: 1,
  coveredCount: 0,
  upcomingCount: 0,
  recentItems: [
    {
      movieId: "m1", title: "Tears of Steel", releaseYear: 2012, imdbId: null, libraryId: "l1", wantedStatus: "missing", wantedReason: "Nothing on disk yet",
      hasFile: false, currentQuality: null, targetQuality: "WEB 1080p", qualityCutoffMet: false, missingSinceUtc: null, lastSearchUtc: "2026-09-29T10:00:00Z",
      nextEligibleSearchUtc: null, lastSearchResult: null, updatedUtc: "2026-09-29T10:00:00Z"
    }
  ]
};

function answer(full: boolean) {
  api.fetchJson.mockImplementation((path: string) => {
    if (path === "/api/settings") return Promise.resolve(emptyPlatformSettingsSnapshot);
    if (path === "/api/setup/progress") return Promise.resolve({ lastCompletedStep: 0, isSkipped: false, isCompleted: false, updatedUtc: "2026-09-30T00:00:00Z" });
    if (path === "/api/movies/wanted") return Promise.resolve(full ? wanted : { ...wanted, totalWanted: 0, recentItems: [] });
    if (path === "/api/series/wanted") return Promise.resolve({ ...wanted, totalWanted: 0, recentItems: [] });
    if (path.startsWith("/api/series/upcoming")) {
      return Promise.resolve(full ? [{ seriesId: "s1", title: "Lioness", startYear: 2023, posterUrl: null, episodeId: "e1", seasonNumber: 3, episodeNumber: 2, episodeTitle: "Second", airDateUtc: new Date(Date.now() + 15 * 3_600_000).toISOString() }] : []);
    }
    if (path.startsWith("/api/dashboard/metrics")) {
      return Promise.resolve({ days: 30, librarySize: full ? [{ date: "2026-09-01", value: 5 }, { date: "2026-09-30", value: 22 }] : [], titlesAdded: [] });
    }
    return Promise.resolve([]);
  });
}

function view(node: React.ReactElement) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{node}</MemoryRouter>
    </QueryClientProvider>
  );
}

/** Words a person reads must never be an internal code or a broken value. */
function expectPlainWords(container: HTMLElement) {
  const text = container.textContent ?? "";
  expect(text).not.toMatch(/undefined|NaN|\bnull\b|\[object/);
  expect(text).not.toMatch(/\b[a-z]+\.[a-z]+\.[a-z]+\b/);
}

beforeEach(() => {
  queryClient.clear();
  api.fetchJson.mockReset();
});

describe("Overview", () => {
  it("says one line per area, each linking into its tab, and lists what needs you with a fix button each", async () => {
    answer(true);
    const { container } = view(<OverviewTab summary={fullSummary()} />);

    const areas = screen.getByRole("region", { name: "Areas" });
    expect(within(areas).getByRole("link", { name: "Library" })).toHaveAttribute("href", "/?tab=library");
    expect(within(areas).getByRole("link", { name: "Downloads" })).toHaveAttribute("href", "/?tab=downloads");
    expect(within(areas).getByRole("link", { name: "Automation" })).toHaveAttribute("href", "/?tab=system");
    expect(within(areas).getByRole("link", { name: "Storage" })).toHaveAttribute("href", "/?tab=system");
    expect(within(areas).getByRole("link", { name: "Retention" })).toHaveAttribute("href", "/?tab=library");
    expect(within(areas).getAllByRole("listitem")).toHaveLength(10);
    expect(within(areas).getByText(/20 movies and 2 shows\. 3 missing, 2 could be upgraded\./)).toBeInTheDocument();
    expect(within(areas).getByText(/3\.9 MB\/s down\. 4 downloading, 1 queued, 2 stalled\./)).toBeInTheDocument();

    const needs = screen.getByRole("region", { name: /^needs you$/i });
    expect(within(needs).getByRole("link", { name: /Fix it in Find & Download: Nyaa\.si isn't answering/ })).toHaveAttribute("href", "/indexers/indexers");
    expect(within(needs).getByRole("link", { name: /Fix it in Downloads: 2 downloads are stalled/ })).toHaveAttribute("href", "/?tab=downloads");
    expect(within(needs).getByRole("link", { name: /Fix it in Downloads: 3 imports need a look/ })).toHaveAttribute("href", "/?tab=downloads");
    // Every row has its own button, with its own name.
    const buttons = within(needs).getAllByRole("link");
    expect(new Set(buttons.map((button) => button.getAttribute("aria-label"))).size).toBe(buttons.length);

    const latest = screen.getByRole("region", { name: "Latest" });
    expect(within(latest).getByText(/Imported The Little Shop of Horrors/)).toBeInTheDocument();
    expect(within(latest).getByText(/Imported into the library/)).toBeInTheDocument();
    expect(within(latest).getByRole("link", { name: /Little Shop/ })).toHaveAttribute("href", "/movies/m9");
    expectPlainWords(container);
    await waitFor(() => expect(api.fetchJson).toHaveBeenCalled());
  });

  it("does not say the same number twice on the one screen", () => {
    const text = buildAreaLines(fullSummary()).map((line) => line.text).join(" ");
    // Speed is said once, in Downloads, and the library's size once, in Library.
    expect(text.match(/MB\/s/g) ?? []).toHaveLength(1);
    expect(text.match(/20 movies/g) ?? []).toHaveLength(1);
  });

  it("says something true for every area on a new install", () => {
    answer(false);
    const lines = buildAreaLines(emptySummary());
    expect(lines).toHaveLength(10);
    for (const line of lines) expect(line.text.length).toBeGreaterThan(10);
    expect(lines.find((line) => line.id === "downloads")?.text).toBe("No download client has reported yet.");
    expect(lines.find((line) => line.id === "library")?.action).toEqual({ label: "Add a movie", to: "/movies?add=true" });
    const { container } = view(<OverviewTab summary={emptySummary()} />);
    expect(screen.getByRole("link", { name: "Add a movie" })).toHaveAttribute("href", "/movies?add=true");
    expect(screen.getByText(/Nothing needs you/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing has happened yet/)).toBeInTheDocument();
    expectPlainWords(container);
  });

  it("names an area that could not be read instead of leaving it blank", () => {
    const summary = { ...fullSummary(), retention: null };
    expect(buildAreaLines(summary).find((line) => line.id === "retention")?.text).toMatch(/Not available just now/);
  });

  it("counts what needs you for the tab's badge, and none on a new install", () => {
    expect(computeAttention(emptySummary()).counts.overview).toBe(0);
    expect(computeAttention(undefined).counts.overview).toBe(0);
    expect(computeAttention(fullSummary()).items.map((row) => row.id)).toEqual(
      expect.arrayContaining(["health:h1", "health:h2", "health:more", "stalled", "failed-downloads", "recovery", "handoffs", "failed-jobs", "lanes"])
    );
    expect(computeAttention(fullSummary(), {}, [{ id: "s", title: "t", text: "x", to: "/", action: "Go" }]).counts.overview).toBe(computeAttention(fullSummary()).counts.overview + 1);
  });
});

describe("Library", () => {
  it("shows the counts once and the panels behind them, with links to where each is acted on", async () => {
    answer(true);
    const { container } = view(<LibraryTab summary={fullSummary()} />);

    expect(screen.getByRole("link", { name: /Missing/ })).toHaveAttribute("href", "/search-cycles/missing");
    expect(screen.getByRole("link", { name: /Upgradable/ })).toHaveAttribute("href", "/search-cycles/upgrades");

    const wantedPanel = screen.getByRole("region", { name: "Wanted" });
    expect(await within(wantedPanel).findByRole("link", { name: "Tears of Steel (2012)" })).toHaveAttribute("href", "/movies/m1");
    expect(within(wantedPanel).getByRole("button", { name: "Search now: Tears of Steel (2012)" })).toBeInTheDocument();

    const coming = screen.getByRole("region", { name: /^coming up/i });
    expect(await within(coming).findByRole("link", { name: /Lioness/ })).toHaveAttribute("href", "/tv/s1");
    expect(within(coming).getByText("in 15 h")).toBeInTheDocument();

    expect(within(screen.getByRole("region", { name: "Retention" })).getByRole("link", { name: /Big Buck Bunny/ })).toHaveAttribute("href", "/movies/m1");
    expect(within(screen.getByRole("region", { name: "Subtitles" })).getByText("14")).toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: "Collections" })).getByText(/1 collection could not update/)).toBeInTheDocument();
    expect(await screen.findByText(/titles added over the last 30 days/)).toBeInTheDocument();
    expectPlainWords(container);
  });

  it("says something true in every panel on a new install, and draws no growth chart", async () => {
    answer(false);
    const { container } = view(<LibraryTab summary={emptySummary()} />);
    expect(screen.getByText(/Nothing to look for yet/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing to count until files are in the library/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing airs in the next 72 hours/)).toBeInTheDocument();
    expect(screen.getByText(/Nothing is due to leave/)).toBeInTheDocument();
    expect(screen.getByText(/No titles are waiting for subtitles/)).toBeInTheDocument();
    expect(screen.getByText(/No collections followed/)).toBeInTheDocument();
    expect(screen.getByText(/No request server or lists connected yet/)).toBeInTheDocument();
    await waitFor(() => expect(api.fetchJson).toHaveBeenCalled());
    expect(screen.queryByText(/titles added over the last 30 days/)).toBeNull();
    expectPlainWords(container);
  });

  it("counts a collection that cannot update and a request server that is not answering", () => {
    expect(computeAttention(emptySummary()).counts.library).toBe(0);
    expect(computeAttention(undefined).counts.library).toBe(0);
    expect(computeAttention(fullSummary()).counts.library).toBe(1);
    const failing = fullSummary();
    failing.requests!.seerrFailing = 1;
    expect(computeAttention(failing).counts.library).toBe(2);
  });
});
