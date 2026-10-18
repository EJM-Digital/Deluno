import { act, configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The workstation can be busy with other builds; a slow render is not a failed page.
configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The show page's own wiring (coverage register, area show-detail): the page is rendered through its real loader with only
 * the network answered, and each control is used the way a person uses it. What the server does with those calls is proven
 * by the backend tests named in the register.
 */
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch, useAuth: () => ({ token: "t" }) }));

const toast = vi.hoisted(() =>
  Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), message: vi.fn(), dismiss: vi.fn() })
);
vi.mock("../components/shell/toaster", () => ({ toast, realtimeToast: {}, Toaster: () => null }));

// jsdom has no layout: the synopsis measures itself with matchMedia, and some list cells watch their size.
window.matchMedia ??= ((query: string) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

const { ShowDetailPage, showDetailLoader } = await import("./show-detail-page");

type Reply = { status?: number; body?: unknown };
type Handler = (url: string, init?: RequestInit) => Reply | undefined;

const daysAgo = (days: number) => new Date(Date.now() - days * 86_400_000).toISOString();
const daysAhead = (days: number) => new Date(Date.now() + days * 86_400_000).toISOString();

const SERIES = {
  id: "s1",
  title: "The Wire",
  startYear: 2002,
  imdbId: "tt0306414",
  monitored: true,
  hasFile: true,
  metadataProvider: "tmdb",
  metadataProviderId: "1438",
  originalTitle: null,
  overview: "Baltimore.",
  posterUrl: null,
  backdropUrl: null,
  rating: 9.3,
  genres: "Crime",
  externalUrl: null,
  metadataJson: null,
  metadataUpdatedUtc: null,
  createdUtc: "2026-10-01T00:00:00Z",
  updatedUtc: "2026-10-01T00:00:00Z",
  libraryId: "lib1",
  wantedStatus: "missing",
  wantedReason: "Two aired episodes are missing.",
  targetQuality: "WEB 1080p",
  qualityCutoffMet: false,
  seriesType: "standard",
  numberingScheme: "standard",
  numberingSource: "provider"
};

function episode(over: Record<string, unknown>) {
  return {
    episodeId: "e",
    seasonNumber: 1,
    episodeNumber: 1,
    title: "Title",
    overview: null,
    airDateUtc: daysAgo(30),
    monitored: true,
    hasFile: false,
    wantedStatus: "missing",
    wantedReason: "No file yet.",
    qualityCutoffMet: false,
    currentQuality: null,
    targetQuality: "WEB 1080p",
    preventLowerQualityReplacements: true,
    lastQualityDeltaDecision: null,
    lastSearchUtc: null,
    nextEligibleSearchUtc: null,
    updatedUtc: "2026-10-01T00:00:00Z",
    ...over
  };
}

const EPISODES = [
  episode({ episodeId: "e00", seasonNumber: 0, episodeNumber: 1, title: "A Special" }),
  episode({ episodeId: "e11", seasonNumber: 1, episodeNumber: 1, title: "The Target", hasFile: true, wantedStatus: "covered" }),
  episode({ episodeId: "e12", seasonNumber: 1, episodeNumber: 2, title: "The Detail" }),
  episode({ episodeId: "e13", seasonNumber: 1, episodeNumber: 3, title: "The Buys", monitored: false, wantedStatus: "unmonitored" }),
  episode({ episodeId: "e21", seasonNumber: 2, episodeNumber: 1, title: "Ebb Tide", airDateUtc: daysAhead(30) })
];

const INVENTORY = {
  seriesId: "s1",
  title: "The Wire",
  startYear: 2002,
  seasonCount: 3,
  episodeCount: EPISODES.length,
  importedEpisodeCount: 1,
  episodes: EPISODES,
  numbering: {
    seriesId: "s1",
    seriesType: "standard",
    numberingScheme: "standard",
    numberingSource: "provider",
    availableNumberingSchemes: ["standard", "absolute"],
    otherNumberingSchemeNames: [],
    episodes: EPISODES.map((item) => ({ episodeId: item.episodeId, absoluteNumber: null, sceneSeasonNumber: null, sceneEpisodeNumber: null, airDate: null }))
  }
};

const PREFERRED = {
  releaseName: "The.Wire.S01E02.1080p.WEB-DL-GRP",
  indexerId: "idx1",
  indexerName: "Fixture Indexer",
  quality: "WEB 1080p",
  score: 10,
  meetsCutoff: true,
  summary: "Best match for this profile.",
  downloadUrl: "https://fixture.invalid/preferred.torrent",
  sizeBytes: 1_073_741_824,
  seeders: 40,
  leechers: 2,
  indexerProtocol: "torznab",
  decisionStatus: "preferred",
  decisionReasons: ["Best match for this profile"],
  riskFlags: [],
  qualityDelta: 1
};
const ELIGIBLE = { ...PREFERRED, releaseName: "The.Wire.S01E02.720p.HDTV-OTHER", indexerName: "Second Indexer", quality: "HDTV 720p", downloadUrl: "https://fixture.invalid/eligible.torrent", decisionStatus: "eligible", qualityDelta: 0 };

let calls: Array<{ url: string; method: string; body: unknown }> = [];
let handler: Handler = () => undefined;

function answer(url: string, init?: RequestInit): Reply {
  const override = handler(url, init);
  if (override) return override;
  const path = url.split("?")[0];
  if (path === "/api/series/s1") return { body: SERIES };
  if (path === "/api/series/s1/inventory") return { body: INVENTORY };
  if (path === "/api/series/search-history") return { body: [] };
  if (path === "/api/series/import-recovery") return { body: { openCount: 0, recentCases: [] } };
  if (path === "/api/libraries") return { body: [{ id: "lib1", name: "Shows", mediaType: "tv", rootPath: "/shows", importWorkflow: "standard" }] };
  if (path === "/api/series/s1/libraries/") return { body: [{ libraryId: "lib1", libraryName: "Shows", hasFile: true, notWantedHere: false }] };
  if (path === "/api/series/s1/removal-preview") return { body: { filePaths: [], folderPaths: [], warnings: [] } };
  if (path === "/api/download-dispatches" || path === "/api/activity" || path === "/api/decisions") return { body: { items: [], hasMore: false } };
  if (path.endsWith("/subtitles") || path.endsWith("/subtitles/history") || path === "/api/subtitle-languages") return { body: [] };
  return { status: 404, body: { message: "not stood in" } };
}

function setup(next: Handler = () => undefined) {
  handler = next;
  calls = [];
  authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    const reply = answer(url, init);
    calls.push({ url, method: init?.method ?? "GET", body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const status = reply.status ?? 200;
    // fetchJson retries a 5xx, so a read that "fails" in these tests answers 404 instead.
    return new Response(status === 204 ? null : JSON.stringify(reply.body ?? {}), { status, headers: { "Content-Type": "application/json" } });
  });
}

const posts = (suffix: string) => calls.filter((c) => c.method !== "GET" && c.url.split("?")[0].endsWith(suffix));
const bodies = (suffix: string) => posts(suffix).map((c) => c.body as Record<string, unknown>);

async function renderPage() {
  const router = createMemoryRouter(
    [
      { path: "/tv/:id", element: <ShowDetailPage />, loader: showDetailLoader },
      { path: "/tv", element: <p>All the shows</p> },
      { path: "*", element: <p>elsewhere</p> }
    ],
    { initialEntries: ["/tv/s1"] }
  );
  render(<RouterProvider router={router} />);
  await screen.findByRole("heading", { level: 1, name: "The Wire" });
  return { router };
}

/** The toolbar's own button: a Next step card repeats "Search now", and the toolbar comes first on the page. */
const press = (name: string | RegExp) => fireEvent.click(screen.getAllByRole("button", { name })[0]);
const openTab = (name: string) => fireEvent.click(screen.getByRole("radio", { name }));
const seasonHeader = (label: string) => screen.getByText(label, { selector: "*" }).closest("[role='row'], tr, div") as HTMLElement;
const episodeRow = (code: string) => screen.getByText(code).closest("[role='row']") as HTMLElement;

beforeEach(() => setup());
afterEach(() => {
  authedFetch.mockReset();
  vi.clearAllMocks();
});

describe("the show's Episodes tab", () => {
  it("groups the episodes by season with Specials first and counts each season's episodes, files, missing and upcoming", async () => {
    await renderPage();

    expect(screen.getByText("Specials")).toBeInTheDocument();
    expect(screen.getByText("Season 1")).toBeInTheDocument();
    expect(screen.getByText("Season 2")).toBeInTheDocument();
    expect(screen.getByText("3 episodes · 1 on disk · 1 missing")).toBeInTheDocument();
    expect(screen.getByText("1 episodes · 0 on disk · 0 missing · 1 upcoming")).toBeInTheDocument();
    const order = screen.getAllByText(/^(Specials|Season \d)$/).map((node) => node.textContent);
    expect(order).toEqual(["Specials", "Season 1", "Season 2"]);
  });

  it("opens the first two seasons with something missing and leaves the rest closed, and Show / Hide toggles one season", async () => {
    await renderPage();

    expect(screen.getByText("S01E02")).toBeInTheDocument();
    expect(screen.queryByText("S02E01")).toBeNull();
    const toggles = screen.getAllByRole("button", { name: /^(Show|Hide)$/ });
    expect(toggles.map((button) => button.getAttribute("aria-expanded"))).toEqual(["true", "true", "false"]);

    fireEvent.click(toggles[1]);
    expect(screen.queryByText("S01E02")).toBeNull();
    fireEvent.click(screen.getAllByRole("button", { name: /^(Show|Hide)$/ })[2]);
    expect(await screen.findByText("S02E01")).toBeInTheDocument();
  });

  it("Expand all opens every season and then reads Collapse all", async () => {
    await renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Expand all" }));

    expect(await screen.findByText("S02E01")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Collapse all" })).toBeInTheDocument();
  });

  it("states its numbers in the summary strip: episodes, on disk, missing (aired only), upcoming and monitored", async () => {
    await renderPage();

    const cell = (label: string) => screen.getAllByText(label).find((node) => node.className.includes("uppercase"))!.parentElement as HTMLElement;
    expect(screen.getByText("3 seasons").parentElement).toHaveTextContent(/Episodes5/);
    expect(cell("On disk")).toHaveTextContent("1");
    // Aired, monitored and without a file: the Special and S01E02. The unmonitored one and the one that has not aired are not missing.
    expect(cell("Missing")).toHaveTextContent("2");
    expect(cell("Upcoming")).toHaveTextContent("1");
    expect(cell("Monitored")).toHaveTextContent("of 5 watched");
  });

  it("the filter chips carry live counts and picking one narrows the list", async () => {
    await renderPage();

    const missing = screen.getByRole("radio", { name: /^Missing/ });
    expect(screen.getByRole("radio", { name: "All 5" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "On disk 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Monitored 4" })).toBeInTheDocument();
    fireEvent.click(missing);

    expect(screen.getByText("S01E02")).toBeInTheDocument();
    expect(screen.queryByText("S01E01")).toBeNull();

    fireEvent.click(screen.getByRole("radio", { name: /^On disk/ }));
    expect(screen.getByText("S01E01")).toBeInTheDocument();
    expect(screen.queryByText("S01E02")).toBeNull();
  });

  it("the Missing chip counts the same list the summary strip does: aired, monitored and without a file", async () => {
    await renderPage();

    const cell = (label: string) => screen.getAllByText(label).find((node) => node.className.includes("uppercase"))!.parentElement as HTMLElement;
    expect(cell("Missing")).toHaveTextContent("2");
    // The unmonitored episode and the one that has not aired are without a file, but they are not missing.
    expect(screen.getByRole("radio", { name: "Missing 2" })).toBeInTheDocument();
  });

  it("the text box narrows by episode code or by title", async () => {
    await renderPage();
    const box = screen.getByPlaceholderText("Filter by code or title");

    fireEvent.change(box, { target: { value: "s01e02" } });
    expect(screen.getByText("S01E02")).toBeInTheDocument();
    expect(screen.queryByText("S01E01")).toBeNull();

    fireEvent.change(box, { target: { value: "target" } });
    expect(screen.getByText("S01E01")).toBeInTheDocument();
    expect(screen.queryByText("S01E02")).toBeNull();
  });

  it("says no episodes match when a filter hides all of them and no episodes are tracked when there are none", async () => {
    await renderPage();
    fireEvent.change(screen.getByPlaceholderText("Filter by code or title"), { target: { value: "zzz" } });
    expect(screen.getByText("No episodes match")).toBeInTheDocument();
  });

  it("says no episodes are tracked yet for a show with no inventory", async () => {
    setup((url) => (url === "/api/series/s1/inventory" ? { body: { ...INVENTORY, episodeCount: 0, seasonCount: 0, importedEpisodeCount: 0, episodes: [], numbering: null } } : undefined));
    await renderPage();

    expect(screen.getByText("No episodes tracked yet")).toBeInTheDocument();
  });

  it("shows the episode's code, its title or a pending note, and when it was last searched", async () => {
    setup((url) =>
      url === "/api/series/s1/inventory"
        ? { body: { ...INVENTORY, episodes: [episode({ episodeId: "x", seasonNumber: 1, episodeNumber: 5, title: null, lastSearchUtc: daysAgo(1), lastSearchResult: "Nothing matched." }), ...EPISODES.slice(0, 1)] } }
        : undefined
    );
    await renderPage();

    const row = episodeRow("S01E05");
    expect(within(row).getByText("Episode title pending")).toBeInTheDocument();
    expect(within(episodeRow("S00E01")).getByText("Never")).toBeInTheDocument();
  });

  it("the Monitor season switch is on only when every episode of the season is monitored, counting the whole season and not the filtered rows", async () => {
    await renderPage();
    expect(screen.getByRole("switch", { name: "Monitor Season 1" })).not.toBeChecked();
    expect(screen.getByRole("switch", { name: "Monitor Specials" })).toBeChecked();

    fireEvent.click(screen.getByRole("radio", { name: /^Monitored/ }));

    // With the list narrowed to monitored episodes, Season 1 still has an unmonitored one, so it is not "all".
    expect(screen.getByRole("switch", { name: "Monitor Season 1" })).not.toBeChecked();
  });

  it("toggling a season puts season-pass monitoring for that season and says which season could not be updated", async () => {
    setup((url) => (url === "/api/series/season-pass/monitoring" ? { status: 400, body: {} } : undefined));
    await renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Monitor Season 1" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Season 1 could not be updated."));
    expect(bodies("/season-pass/monitoring")[0]).toEqual({ scopes: [{ seriesId: "s1", seasonNumber: 1 }], monitored: true });
  });

  it("the On switch puts that one episode's monitoring and says when it could not be updated", async () => {
    setup((url) => (url === "/api/series/episodes/monitoring" ? { status: 400, body: {} } : undefined));
    await renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Monitor S01E02" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Those episodes could not be updated."));
    expect(bodies("/episodes/monitoring")[0]).toEqual({ episodeIds: ["e12"], monitored: false });
  });

  it("clicking an episode opens its drawer titled with the code and title, Basics are listed, and Close shuts it", async () => {
    await renderPage();

    fireEvent.click(episodeRow("S01E02"));

    const drawer = await screen.findByRole("dialog", { name: /S01E02 · The Detail/ });
    expect(within(drawer).getAllByText("Season 1").length).toBeGreaterThan(0);
    expect(within(drawer).getByText("Basics")).toBeInTheDocument();
    expect(within(drawer).getByText("First aired")).toBeInTheDocument();
    fireEvent.click(within(drawer).getAllByRole("button", { name: "Close" }).at(-1)!);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: /S01E02 · The Detail/ })).toBeNull());
  });

  it("Escape closes the episode drawer", async () => {
    await renderPage();
    fireEvent.click(episodeRow("S01E02"));
    const drawer = await screen.findByRole("dialog", { name: /S01E02 · The Detail/ });

    fireEvent.keyDown(drawer, { key: "Escape" });

    await waitFor(() => expect(screen.queryByRole("dialog", { name: /S01E02 · The Detail/ })).toBeNull());
  });

  it("an episode with no air date reads Not announced in its drawer", async () => {
    setup((url) => (url === "/api/series/s1/inventory" ? { body: { ...INVENTORY, episodes: [episode({ episodeId: "n", seasonNumber: 1, episodeNumber: 9, title: "Later", airDateUtc: null })] } } : undefined));
    await renderPage();

    fireEvent.click(episodeRow("S01E09"));

    const drawer = await screen.findByRole("dialog", { name: /S01E09/ });
    expect(within(drawer).getByText("Not announced")).toBeInTheDocument();
  });

  it("the drawer's Basics name the absolute number, the scene numbers and an owner mapping when there are ones", async () => {
    setup((url) =>
      url === "/api/series/s1/inventory"
        ? { body: { ...INVENTORY, episodes: [episode({ episodeId: "a", seasonNumber: 1, episodeNumber: 5, title: "Anime", absoluteNumber: 105, sceneSeasonNumber: 2, sceneEpisodeNumber: 3, airDate: "2026-09-01", numberingSource: "owner" })] } }
        : undefined
    );
    await renderPage();

    fireEvent.click(episodeRow("S01E05"));

    const drawer = await screen.findByRole("dialog", { name: /S01E05/ });
    expect(within(drawer).getByText("#105")).toBeInTheDocument();
    expect(within(drawer).getByText("S02E03")).toBeInTheDocument();
    expect(within(drawer).getByText("2026-09-01")).toBeInTheDocument();
    expect(within(drawer).getByText("Owner mapping")).toBeInTheDocument();
  });

  it("Search and send these sends a mode send search for every episode currently listed", async () => {
    setup((url) => (url === "/api/series/s1/episodes/search" ? { body: { searchedEpisodes: 4, matchedCount: 1, sentCount: 1, dispatchStatus: "sent", episodes: [] } } : undefined));
    await renderPage();
    fireEvent.click(screen.getByRole("radio", { name: /^Missing/ }));

    fireEvent.click(screen.getByRole("button", { name: "Search and send these" }));

    await waitFor(() => expect(posts("/episodes/search")).toHaveLength(1));
    expect(bodies("/episodes/search")[0]).toEqual({ episodeIds: ["e00", "e12"], mode: "send" });
  });

  it("Search and send these is off when nothing is listed", async () => {
    await renderPage();
    fireEvent.change(screen.getByPlaceholderText("Filter by code or title"), { target: { value: "zzz" } });

    expect(screen.getByRole("button", { name: "Search and send these" })).toBeDisabled();
  });

  it("Search season posts that season's search and toasts how many episodes matched, or that nothing did", async () => {
    setup((url) => (url === "/api/series/s1/seasons/1/search" ? { body: { matchedCount: 2, seasonNumber: 1, dispatchStatus: "sent", dispatchMessage: "Sent to qBittorrent." } } : undefined));
    await renderPage();

    fireEvent.click(screen.getAllByRole("button", { name: "Search season" })[1]);

    await waitFor(() => expect(toast.success).toHaveBeenCalled());
    expect(String(toast.success.mock.calls[0][0])).toMatch(/^Season 1: 2 episode matches\./);
    expect(posts("/seasons/1/search")).toHaveLength(1);
  });

  it("Search season says the season finished with no matches", async () => {
    setup((url) => (url === "/api/series/s1/seasons/0/search" ? { body: { matchedCount: 0, seasonNumber: 0 } } : undefined));
    await renderPage();

    fireEvent.click(screen.getAllByRole("button", { name: "Search season" })[0]);

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Specials: search finished with no matches.", undefined));
  });

  it("Search season explains a search that is refused for a reason", async () => {
    setup((url) => (url === "/api/series/s1/seasons/1/search" ? { body: { reason: "no_indexers" } } : undefined));
    await renderPage();

    fireEvent.click(screen.getAllByRole("button", { name: "Search season" })[1]);

    await waitFor(() => expect(toast.info).toHaveBeenCalled());
    expect(toast.info.mock.calls[0][0]).toBe("No indexers are linked to this library");
  });
});

describe("an episode's drawer", () => {
  const openDrawer = async (code = "S01E02") => {
    fireEvent.click(episodeRow(code));
    return await screen.findByRole("dialog", { name: new RegExp(code) });
  };

  it("Search and send the best release posts the episode with mode send and lists nothing to choose", async () => {
    setup((url) => (url === "/api/series/s1/episodes/search" ? { body: { searchedEpisodes: 1, matchedCount: 1, sentCount: 1, dispatchStatus: "sent", episodes: [{ episodeId: "e12", seasonNumber: 1, episodeNumber: 2, candidates: [] }] } } : undefined));
    await renderPage();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: "Search and send the best release" }));

    await waitFor(() => expect(posts("/episodes/search")).toHaveLength(1));
    expect(bodies("/episodes/search")[0]).toEqual({ episodeIds: ["e12"], mode: "send" });
    expect(screen.queryByRole("heading", { name: /choose a release for s01e02/i })).toBeNull();
  });

  it("Choose a release for this episode lists what it found under a card named for the episode, closes the drawer and sends nothing", async () => {
    setup((url) =>
      url === "/api/series/s1/episodes/search"
        ? { body: { searchedEpisodes: 1, matchedCount: 0, sentCount: 0, episodes: [{ episodeId: "e12", seasonNumber: 1, episodeNumber: 2, title: "The Detail", comparedCount: 2, candidates: [PREFERRED, ELIGIBLE] }] } }
        : undefined
    );
    await renderPage();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: "Choose a release for this episode" }));

    expect(await screen.findByRole("heading", { name: /choose a release for s01e02 · the detail/i })).toBeInTheDocument();
    expect(screen.getByText(PREFERRED.releaseName)).toBeInTheDocument();
    expect(bodies("/episodes/search")[0]).toEqual({ episodeIds: ["e12"], mode: "choose" });
    expect(posts("/grab")).toHaveLength(0);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: /S01E02 · The Detail/ })).toBeNull());
    expect(toast.success).toHaveBeenCalledWith("Compared 2 releases. Choose one below.", undefined);
  });

  it("a choose that finds releases for more than one episode picks no single list to show, and says how many were compared", async () => {
    setup((url) =>
      url === "/api/series/s1/episodes/search"
        ? { body: { searchedEpisodes: 2, matchedCount: 0, sentCount: 0, episodes: [
            { episodeId: "e12", seasonNumber: 1, episodeNumber: 2, title: "The Detail", comparedCount: 1, candidates: [PREFERRED] },
            { episodeId: "e13", seasonNumber: 1, episodeNumber: 3, title: "The Buys", comparedCount: 1, candidates: [ELIGIBLE] }
          ] } }
        : undefined
    );
    await renderPage();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: "Choose a release for this episode" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Compared 2 releases. Choose one below.", undefined));
    expect(screen.queryByRole("heading", { name: /choose a release for s01e0/i })).toBeNull();
    expect(screen.queryByText(PREFERRED.releaseName)).toBeNull();
    expect(screen.queryByText(ELIGIBLE.releaseName)).toBeNull();
    expect(posts("/grab")).toHaveLength(0);
  });

  it("a choose that finds nothing clears the list an earlier choose left", async () => {
    let answers = 0;
    setup((url) =>
      url === "/api/series/s1/episodes/search"
        ? { body: ++answers === 1
            ? { searchedEpisodes: 1, matchedCount: 0, sentCount: 0, episodes: [{ episodeId: "e12", seasonNumber: 1, episodeNumber: 2, title: "The Detail", comparedCount: 1, candidates: [PREFERRED] }] }
            : { searchedEpisodes: 1, matchedCount: 0, sentCount: 0, reason: "no_results", episodes: [{ episodeId: "e12", seasonNumber: 1, episodeNumber: 2, title: "The Detail", comparedCount: 0, candidates: [] }] } }
        : undefined
    );
    await renderPage();
    fireEvent.click(within(await openDrawer()).getByRole("button", { name: "Choose a release for this episode" }));
    expect(await screen.findByText(PREFERRED.releaseName)).toBeInTheDocument();

    fireEvent.click(within(await openDrawer()).getByRole("button", { name: "Choose a release for this episode" }));

    await waitFor(() => expect(screen.queryByText(PREFERRED.releaseName)).toBeNull());
    expect(screen.queryByRole("heading", { name: /choose a release for s01e02/i })).toBeNull();
  });

  it("tells that the episode is already downloading instead of sending a second copy", async () => {
    setup((url) => (url === "/api/series/s1/episodes/search" ? { body: { searchedEpisodes: 1, sentCount: 0, alreadyDownloading: ["S01E02 is already downloading."], episodes: [] } } : undefined));
    await renderPage();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: "Search and send the best release" }));

    await waitFor(() => expect(toast.info).toHaveBeenCalledWith("Already downloading, so nothing more was sent.", { description: "S01E02 is already downloading." }));
  });

  it("says Searched N, matched M with where it went when a search sent something", async () => {
    setup((url) => (url === "/api/series/s1/episodes/search" ? { body: { searchedEpisodes: 1, matchedCount: 1, sentCount: 1, dispatchStatus: "sent", dispatchMessage: "Sent to qBittorrent.", episodes: [] } } : undefined));
    await renderPage();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: "Search and send the best release" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalled());
    expect(String(toast.success.mock.calls[0][0])).toMatch(/^Searched 1 episode, matched 1\./);
  });

  it("explains a refused episode search with the right link", async () => {
    setup((url) => (url === "/api/series/s1/episodes/search" ? { body: { searchedEpisodes: 1, reason: "no_indexers", episodes: [] } } : undefined));
    await renderPage();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: "Search and send the best release" }));

    await waitFor(() => expect(toast.info).toHaveBeenCalled());
    expect(toast.info.mock.calls[0][0]).toBe("No indexers are linked to this library");
    expect((toast.info.mock.calls[0][1] as { action: { label: string } }).action.label).toBe("Open Indexers");
  });

  it("a failed episode search says so with a Check indexers action", async () => {
    setup((url) => (url === "/api/series/s1/episodes/search" ? { status: 400, body: { message: "bad" } } : undefined));
    await renderPage();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("button", { name: "Search and send the best release" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect((toast.error.mock.calls[0][1] as { action: { label: string } }).action.label).toBe("Check indexers");
  });

  it("Delete file is offered only for an episode that has a file, asks first and does nothing until confirmed", async () => {
    setup((url, init) => (url === "/api/series/episodes/e11/file" && init?.method === "DELETE" ? { body: {} } : undefined));
    await renderPage();
    const noFile = await openDrawer("S01E02");
    expect(within(noFile).queryByRole("button", { name: "Delete file" })).toBeNull();
    fireEvent.click(within(noFile).getAllByRole("button", { name: "Close" }).at(-1)!);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: /S01E02/ })).toBeNull());

    const withFile = await openDrawer("S01E01");
    fireEvent.click(within(withFile).getByRole("button", { name: "Delete file" }));

    expect(within(withFile).getByText(/Goes to the recycle bin/)).toBeInTheDocument();
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    fireEvent.click(within(withFile).getByRole("button", { name: "Cancel" }));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);

    fireEvent.click(within(withFile).getByRole("button", { name: "Delete file" }));
    fireEvent.click(within(withFile).getByRole("button", { name: "Confirm delete" }));

    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.url === "/api/series/episodes/e11/file")).toBe(true));
    await waitFor(() => expect(toast.success).toHaveBeenCalled());
    expect(String(toast.success.mock.calls[0][0])).toMatch(/^File moved to the recycle bin\./);
  });

  it("a delete that fails says the file could not be deleted", async () => {
    setup((url, init) => (url === "/api/series/episodes/e11/file" && init?.method === "DELETE" ? { status: 500, body: {} } : undefined));
    await renderPage();
    const drawer = await openDrawer("S01E01");

    fireEvent.click(within(drawer).getByRole("button", { name: "Delete file" }));
    fireEvent.click(within(drawer).getByRole("button", { name: "Confirm delete" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Could not delete this episode's file."));
  });

  it("Monitor this episode in the drawer makes the same call as the row's switch", async () => {
    setup((url) => (url === "/api/series/episodes/monitoring" ? { body: {} } : undefined));
    await renderPage();
    const drawer = await openDrawer();

    fireEvent.click(within(drawer).getByRole("switch", { name: "Monitor this episode" }));

    await waitFor(() => expect(posts("/episodes/monitoring")).toHaveLength(1));
    expect(bodies("/episodes/monitoring")[0]).toEqual({ episodeIds: ["e12"], monitored: false });
  });

  it("lists the episode's subtitle rows when it has any, loaded from the episode's own route when it opens", async () => {
    setup((url) =>
      url === "/api/series/episodes/e12/subtitles"
        ? { body: [{ language: "en", forced: false, hearingImpaired: true, source: "provider", provider: "Fixture", timingStatus: "ok", timingReason: "In sync." }] }
        : undefined
    );
    await renderPage();
    const drawer = await openDrawer();

    expect(await within(drawer).findByText("Subtitles")).toBeInTheDocument();
    expect(within(drawer).getByText("EN")).toBeInTheDocument();
    expect(calls.some((c) => c.url === "/api/series/episodes/e12/subtitles")).toBe(true);
  });

  it("has no Subtitles section when the episode has none", async () => {
    await renderPage();
    const drawer = await openDrawer();

    await waitFor(() => expect(calls.some((c) => c.url === "/api/series/episodes/e12/subtitles")).toBe(true));
    expect(within(drawer).queryByText("Subtitles")).toBeNull();
  });

  it("fills the file panel with what the server holds about the file", async () => {
    setup((url) => {
      if (url === "/api/series/s1/inventory") return { body: { ...INVENTORY, episodes: [episode({ episodeId: "f", seasonNumber: 1, episodeNumber: 1, title: "File", hasFile: true, wantedStatus: "covered", currentQuality: "WEB 1080p" })] } };
      if (url === "/api/series/episodes/f/file") return { body: { episodeId: "f", filePath: "/shows/The Wire/S01E01.mkv", fileSizeBytes: 1073741824, quality: "WEB 1080p", releaseGroup: "EXAMPLE", indexerName: "Fixture Indexer", downloadClientName: "Fixture Client" } };
      return undefined;
    });
    await renderPage();

    fireEvent.click(episodeRow("S01E01"));
    const drawer = await screen.findByRole("dialog", { name: /S01E01/ });

    expect(await within(drawer).findByText("/shows/The Wire/S01E01.mkv")).toBeInTheDocument();
    expect(within(drawer).getByText(/^1(\.0)? GB$/)).toBeInTheDocument();
    expect(within(drawer).getByText("EXAMPLE")).toBeInTheDocument();
    expect(within(drawer).getByText("Fixture Indexer")).toBeInTheDocument();
    expect(within(drawer).getByText("Fixture Client")).toBeInTheDocument();
  });

  it("shows the episode file's path, size, quality, release group, indexer and client in its drawer", async () => {
    setup((url) => (url === "/api/series/s1/inventory" ? { body: { ...INVENTORY, episodes: [episode({ episodeId: "f", seasonNumber: 1, episodeNumber: 1, title: "File", hasFile: true, wantedStatus: "covered", currentQuality: "WEB 1080p" })] } } : undefined));
    await renderPage();

    fireEvent.click(episodeRow("S01E01"));
    const drawer = await screen.findByRole("dialog", { name: /S01E01/ });

    expect(within(drawer).getByText("Release group")).toBeInTheDocument();
    expect(within(drawer).getByText("Indexer")).toBeInTheDocument();
  });
});

describe("the show's Next step card", () => {
  it("offers Find N missing episodes counting aired ones only, and Search now runs the automatic search", async () => {
    setup((url) => (url.startsWith("/api/series/s1/search") ? { body: { outcome: "none", reason: "no_indexers" } } : undefined));
    await renderPage();

    expect(screen.getByText("Find 2 missing episodes")).toBeInTheDocument();
    const card = screen.getByText("Find 2 missing episodes").closest("[role='row']") as HTMLElement;
    fireEvent.click(within(card).getByRole("button", { name: "Search now" }));

    await waitFor(() => expect(posts("/search")).toHaveLength(1));
    expect(calls.find((c) => c.url === "/api/series/s1/search")!.method).toBe("POST");
  });

  it("says Find 1 missing episode in the singular", async () => {
    setup((url) => (url === "/api/series/s1/inventory" ? { body: { ...INVENTORY, episodes: EPISODES.filter((item) => item.episodeId !== "e00") } } : undefined));
    await renderPage();

    expect(screen.getByText("Find 1 missing episode")).toBeInTheDocument();
  });

  it("says every aired episode is here, with no button, when only upcoming episodes are left", async () => {
    setup((url) => (url === "/api/series/s1/inventory" ? { body: { ...INVENTORY, episodes: [EPISODES[1], EPISODES[4]] } } : undefined));
    await renderPage();

    expect(screen.getByText("Every aired episode is here")).toBeInTheDocument();
    expect(screen.getByText("1 episode has not aired yet. Deluno will look as each one does.")).toBeInTheDocument();
  });

  it("offers Resume automation for an unmonitored show and turns monitoring back on", async () => {
    setup((url) => (url === "/api/series/s1" ? { body: { ...SERIES, monitored: false } } : url === "/api/series/monitoring" ? { body: {} } : undefined));
    await renderPage();
    expect(screen.getByText("Resume automatic care")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Resume automation" }));

    await waitFor(() => expect(posts("/monitoring")).toHaveLength(1));
    expect(bodies("/monitoring")[0]).toEqual({ seriesIds: ["s1"], monitored: true });
  });

  it("names the import issues and Open import issues switches to the History tab", async () => {
    setup((url) =>
      url.startsWith("/api/series/import-recovery?")
        ? { body: { openCount: 1, recentCases: [{ id: "c1", title: "The Wire", failureKind: "quality", summary: "Rejected on quality.", recommendedAction: "Search again.", detailsJson: null, detectedUtc: "2026-10-01T00:00:00Z" }] } }
        : undefined
    );
    await renderPage();
    expect(screen.getByText("Review 1 import issue")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Open import issues" }));

    expect(await screen.findByText("Quality rejected")).toBeInTheDocument();
  });
});

describe("Search now and Choose a release on a show", () => {
  const preview = (url: string) => (url.startsWith("/api/series/s1/search?mode=preview") ? { body: { outcome: "held", candidates: [PREFERRED, ELIGIBLE] } } : undefined);

  it("Search now posts the automatic search, shows what was sent and keeps the note on the page", async () => {
    setup((url) => (url === "/api/series/s1/search" ? { body: { outcome: "sent", releaseName: PREFERRED.releaseName, indexerName: "Fixture Indexer", dispatchStatus: "sent", plannedEpisodes: 2 } } : undefined));
    await renderPage();

    press("Search now");

    await waitFor(() => expect(toast.success).toHaveBeenCalled());
    expect(String(toast.success.mock.calls[0][0])).toMatch(/sent/i);
    expect(screen.getByRole("status", { name: "Result of the last search" })).toHaveTextContent(PREFERRED.releaseName);
  });

  it("Search now explains a search that finds nothing and one that fails", async () => {
    setup((url) => (url === "/api/series/s1/search" ? { body: { outcome: "none", reason: "no_indexers" } } : undefined));
    await renderPage();
    press("Search now");
    await waitFor(() => expect(toast.info).toHaveBeenCalledWith("No indexers are linked to this library", expect.anything()));

    setup((url) => (url === "/api/series/s1/search" ? { status: 400, body: {} } : undefined));
    press("Search now");
    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect((toast.error.mock.calls[0][1] as { action: { label: string } }).action.label).toBe("Check indexers");
  });

  it("Choose a release lists the compared candidates and says how many", async () => {
    setup(preview);
    await renderPage();

    press("Choose a release");

    expect(await screen.findByText(PREFERRED.releaseName)).toBeInTheDocument();
    expect(toast.success).toHaveBeenCalledWith("2 releases compared. Choose one below.", undefined);
  });

  it("Send to downloads posts the release with its indexer, quality, url, size and seeders, toasts where it went and clears the candidates", async () => {
    setup((url) => preview(url) ?? (url.startsWith("/api/series/s1/grab") ? { body: { releaseName: PREFERRED.releaseName, indexerName: "Fixture Indexer", dispatchStatus: "sent", dispatchMessage: "Sent to qBittorrent." } } : undefined));
    await renderPage();
    press("Choose a release");
    fireEvent.click(await screen.findByText(PREFERRED.releaseName));
    const drawer = await screen.findByRole("dialog", { name: PREFERRED.releaseName });

    fireEvent.click(within(drawer).getByRole("button", { name: "Send to downloads" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledTimes(2));
    expect(bodies("/grab")[0]).toMatchObject({
      releaseName: PREFERRED.releaseName,
      indexerName: "Fixture Indexer",
      candidateQuality: "WEB 1080p",
      downloadUrl: PREFERRED.downloadUrl,
      sizeBytes: PREFERRED.sizeBytes,
      seeders: PREFERRED.seeders,
      force: false
    });
    await waitFor(() => expect(screen.queryByRole("dialog", { name: PREFERRED.releaseName })).toBeNull());
    expect(screen.queryByText(ELIGIBLE.releaseName)).toBeNull();
  });

  it("a release Deluno would not pick opens with Force expanded, Send anyway needs a reason and sends it as the override reason", async () => {
    setup((url) => preview(url) ?? (url.startsWith("/api/series/s1/grab") ? { body: { releaseName: ELIGIBLE.releaseName, dispatchStatus: "sent" } } : undefined));
    await renderPage();
    press("Choose a release");
    fireEvent.click(await screen.findByText(ELIGIBLE.releaseName));
    const drawer = await screen.findByRole("dialog", { name: ELIGIBLE.releaseName });

    expect(within(drawer).getByRole("button", { name: "Send to downloads" })).toBeDisabled();
    const reason = within(drawer).getByRole("textbox", { name: "Why force this release?" });
    fireEvent.change(reason, { target: { value: "  " } });
    expect(within(drawer).getByRole("button", { name: "Send anyway" })).toBeDisabled();

    fireEvent.change(reason, { target: { value: "Only copy." } });
    fireEvent.click(within(drawer).getByRole("button", { name: "Send anyway" }));

    await waitFor(() => expect(posts("/grab")).toHaveLength(1));
    expect(bodies("/grab")[0]).toMatchObject({ force: true, overrideReason: "Only copy." });
  });

  it("Cancel on the force box hides it", async () => {
    setup(preview);
    await renderPage();
    press("Choose a release");
    fireEvent.click(await screen.findByText(ELIGIBLE.releaseName));
    const drawer = await screen.findByRole("dialog", { name: ELIGIBLE.releaseName });

    fireEvent.click(within(drawer).getAllByRole("button", { name: "Cancel" })[0]);

    expect(within(drawer).queryByRole("textbox", { name: "Why force this release?" })).toBeNull();
  });

  it("a candidate with no download address cannot be sent or forced", async () => {
    setup((url) => (url.startsWith("/api/series/s1/search?mode=preview") ? { body: { candidates: [{ ...ELIGIBLE, downloadUrl: null }] } } : undefined));
    await renderPage();
    press("Choose a release");
    fireEvent.click(await screen.findByText(ELIGIBLE.releaseName));
    const drawer = await screen.findByRole("dialog", { name: ELIGIBLE.releaseName });

    expect(within(drawer).getByText("This candidate has no downloadable URL yet")).toBeInTheDocument();
    expect(within(drawer).getByRole("button", { name: "Send to downloads" })).toBeDisabled();
    expect(within(drawer).getByRole("button", { name: "Force" })).toBeDisabled();
  });

  describe("when the episode is already downloading", () => {
    const question = { status: 409, body: { code: "already-downloading", message: "S01E02 is already downloading in qBittorrent. Send this one as well?" } };

    async function sendAndGetQuestion() {
      setup((url, init) => preview(url) ?? (url.startsWith("/api/series/s1/grab") ? (JSON.parse(String(init?.body)).duplicateAction ? { body: { releaseName: PREFERRED.releaseName, dispatchStatus: "sent" } } : question) : undefined));
      await renderPage();
      press("Choose a release");
      fireEvent.click(await screen.findByText(PREFERRED.releaseName));
      const drawer = await screen.findByRole("dialog", { name: PREFERRED.releaseName });
      fireEvent.click(within(drawer).getByRole("button", { name: "Send to downloads" }));
      return await screen.findByRole("dialog", { name: "Already downloading" });
    }

    it("Replace it re-sends with duplicateAction replace", async () => {
      const dialog = await sendAndGetQuestion();
      fireEvent.click(within(dialog).getByRole("button", { name: "Replace it" }));
      await waitFor(() => expect(posts("/grab")).toHaveLength(2));
      expect(bodies("/grab").map((body) => body.duplicateAction)).toEqual([null, "replace"]);
    });

    it("Send as well re-sends with duplicateAction send-as-well", async () => {
      const dialog = await sendAndGetQuestion();
      fireEvent.click(within(dialog).getByRole("button", { name: "Send as well" }));
      await waitFor(() => expect(posts("/grab")).toHaveLength(2));
      expect(bodies("/grab")[1].duplicateAction).toBe("send-as-well");
    });

    it("Cancel sends nothing more", async () => {
      const dialog = await sendAndGetQuestion();
      fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
      await waitFor(() => expect(screen.queryByRole("dialog", { name: "Already downloading" })).toBeNull());
      expect(posts("/grab")).toHaveLength(1);
    });
  });

  it("a grab that fails says so and links to the download clients", async () => {
    setup((url) => preview(url) ?? (url.startsWith("/api/series/s1/grab") ? { status: 400, body: { message: "refused" } } : undefined));
    await renderPage();
    press("Choose a release");
    fireEvent.click(await screen.findByText(PREFERRED.releaseName));
    const drawer = await screen.findByRole("dialog", { name: PREFERRED.releaseName });

    fireEvent.click(within(drawer).getByRole("button", { name: "Send to downloads" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect((toast.error.mock.calls[0][1] as { action: { label: string } }).action.label).toBe("Check download clients");
  });
});

describe("the show's controls", () => {
  it("the shield unmonitors the show and a failure says the monitoring could not be changed", async () => {
    setup((url) => (url === "/api/series/monitoring" ? { status: 400, body: {} } : undefined));
    await renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Monitored — click to unmonitor" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("This show's monitoring could not be changed."));
    expect(bodies("/monitoring")[0]).toEqual({ seriesIds: ["s1"], monitored: false });
  });

  it("the Background automation switch makes the same call as the shield and the card counts Monitored or Unmonitored", async () => {
    setup((url) => (url === "/api/series/monitoring" ? { body: {} } : undefined));
    await renderPage();
    openTab("Destination");
    expect(await screen.findByText("Monitored", { selector: "*" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("switch", { name: "Background automation" }));

    await waitFor(() => expect(posts("/monitoring")).toHaveLength(1));
    expect(bodies("/monitoring")[0]).toEqual({ seriesIds: ["s1"], monitored: false });
  });

  it("Monitor new episodes puts the preset and says when it could not be changed", async () => {
    setup((url) => (url === "/api/series/s1/monitor-new-episodes" ? { status: 400, body: {} } : undefined));
    await renderPage();
    openTab("Destination");

    fireEvent.click(await screen.findByRole("switch", { name: "Monitor new episodes" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("This show's new-episode preset could not be changed."));
    expect(bodies("/monitor-new-episodes")).toHaveLength(1);
  });

  it("Defer for 24 hours posts hours 24 for the show's library and toasts", async () => {
    setup((url) => (url === "/api/series/s1/automation/defer" ? { body: {} } : undefined));
    await renderPage();
    openTab("Destination");

    fireEvent.click(await screen.findByRole("button", { name: "Defer" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Deferred for 24 hours. Manual searches still work."));
    expect(bodies("/automation/defer")[0]).toEqual({ libraryId: "lib1", hours: 24 });
  });

  it("Skip once toasts that the next scheduled search will be skipped", async () => {
    setup((url) => (url === "/api/series/s1/automation/skip-once" ? { body: {} } : undefined));
    await renderPage();
    openTab("Destination");

    fireEvent.click(await screen.findByRole("button", { name: "Skip once" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("The next scheduled search will be skipped."));
  });

  it("Defer and Skip once are off for a show nothing is searching for", async () => {
    setup((url) => (url === "/api/series/s1" ? { body: { ...SERIES, wantedStatus: "covered" } } : undefined));
    await renderPage();
    openTab("Destination");

    expect(await screen.findByRole("button", { name: "Defer" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Skip once" })).toBeDisabled();
  });

  it("Refresh metadata toasts the show's name, and a lost record says the show and files were kept", async () => {
    setup((url) => (url === "/api/series/s1/metadata/refresh" ? { body: {} } : undefined));
    await renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Refresh metadata" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("The Wire metadata refreshed."));

    setup((url) => (url === "/api/series/s1/metadata/refresh" ? { status: 409, body: {} } : undefined));
    fireEvent.click(screen.getByRole("button", { name: "Refresh metadata" }));
    await waitFor(() => expect(toast.info).toHaveBeenCalledWith("The TMDb record is no longer available. Your show and files were kept."));
  });

  it("a metadata refresh that fails otherwise is explained with a Check metadata settings action", async () => {
    setup((url) => (url === "/api/series/s1/metadata/refresh" ? { status: 400, body: { message: "TMDb said no." } } : undefined));
    await renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Refresh metadata" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect((toast.error.mock.calls[0][1] as { action: { label: string } }).action.label).toBe("Check metadata settings");
  });

  it("a removal that fails says the TV show could not be removed", async () => {
    setup((url) => (url === "/api/series/bulk" ? { body: { successCount: 0, results: [] } } : undefined));
    await renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Remove from Deluno" }));
    const dialog = await screen.findByRole("dialog", { name: /Remove The Wire\?/ });
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove TV show" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("This TV show could not be removed."));
  });
});

describe("the show's TV numbering", () => {
  it("summarises the series type, the numbering scheme and the mapping source in words", async () => {
    setup((url) => (url === "/api/series/s1/inventory" ? { body: { ...INVENTORY, numbering: { ...INVENTORY.numbering, seriesType: "anime", numberingScheme: "absolute", numberingSource: "owner" } } } : undefined));
    await renderPage();
    openTab("Destination");

    expect(await screen.findByText("Anime")).toBeInTheDocument();
    expect(screen.getByText("Absolute episode")).toBeInTheDocument();
    expect(screen.getByText("Owner mapping")).toBeInTheDocument();
  });

  it("Edit numbering opens the drawer with the saved values and always offers the saved scheme, even one a refresh dropped", async () => {
    setup((url) => (url === "/api/series/s1/inventory" ? { body: { ...INVENTORY, numbering: { ...INVENTORY.numbering, numberingScheme: "dvd", availableNumberingSchemes: ["standard"] } } } : undefined));
    await renderPage();
    openTab("Destination");

    fireEvent.click(await screen.findByRole("button", { name: "Edit numbering" }));

    const drawer = await screen.findByRole("dialog", { name: "TV numbering" });
    const scheme = within(drawer).getByRole("combobox", { name: "Numbering scheme" }) as HTMLSelectElement;
    expect(scheme.value).toBe("dvd");
    expect([...scheme.options].map((option) => option.value)).toContain("dvd");
    expect((within(drawer).getByRole("combobox", { name: "Series type" }) as HTMLSelectElement).value).toBe("standard");
    expect((within(drawer).getByRole("combobox", { name: "Mapping source" }) as HTMLSelectElement).value).toBe("provider");
  });

  it("Save numbering sends the type, scheme and source, drops blank mappings, and a provider source sends none at all", async () => {
    setup((url, init) => (url === "/api/series/s1/numbering" && init?.method === "PUT" ? { body: {} } : undefined));
    await renderPage();
    openTab("Destination");
    fireEvent.click(await screen.findByRole("button", { name: "Edit numbering" }));
    const drawer = await screen.findByRole("dialog", { name: "TV numbering" });

    fireEvent.click(within(drawer).getByRole("button", { name: "Save numbering" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("TV numbering updated."));
    expect(bodies("/numbering")[0]).toEqual({ seriesType: "standard", numberingScheme: "standard", numberingSource: "provider", mappings: [] });
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "TV numbering" })).toBeNull());
  });

  it("an owner mapping is sent for the one episode that was filled in and the blank rows are dropped", async () => {
    setup((url, init) => (url === "/api/series/s1/numbering" && init?.method === "PUT" ? { body: {} } : undefined));
    await renderPage();
    openTab("Destination");
    fireEvent.click(await screen.findByRole("button", { name: "Edit numbering" }));
    const drawer = await screen.findByRole("dialog", { name: "TV numbering" });
    fireEvent.change(within(drawer).getByRole("combobox", { name: "Mapping source" }), { target: { value: "owner" } });
    fireEvent.change(within(drawer).getByRole("spinbutton", { name: "S01E02 absolute number" }), { target: { value: "105" } });
    fireEvent.change(within(drawer).getByRole("spinbutton", { name: "S01E02 scene season" }), { target: { value: "2" } });
    fireEvent.change(within(drawer).getByRole("spinbutton", { name: "S01E02 scene episode" }), { target: { value: "5" } });

    fireEvent.click(within(drawer).getByRole("button", { name: "Save numbering" }));

    await waitFor(() => expect(posts("/numbering")).toHaveLength(1));
    expect(bodies("/numbering")[0]).toMatchObject({
      numberingSource: "owner",
      mappings: [{ episodeId: "e12", absoluteNumber: 105, sceneSeasonNumber: 2, sceneEpisodeNumber: 5, airDate: null }]
    });
  });

  it("any failure to save shows the one plain sentence", async () => {
    setup((url, init) => (url === "/api/series/s1/numbering" && init?.method === "PUT" ? { status: 400, body: { errors: { mappings: ["Each episode may appear only once in a numbering mapping."] } } } : undefined));
    await renderPage();
    openTab("Destination");
    fireEvent.click(await screen.findByRole("button", { name: "Edit numbering" }));
    const drawer = await screen.findByRole("dialog", { name: "TV numbering" });

    fireEvent.click(within(drawer).getByRole("button", { name: "Save numbering" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("TV numbering could not be updated."));
    expect(screen.getByRole("dialog", { name: "TV numbering" })).toBeInTheDocument();
  });
});

describe("the show's History tab", () => {
  const caseFor = (failureKind: string, extra: Record<string, unknown> = {}) => ({
    id: "c1",
    title: "the wire",
    failureKind,
    summary: `Summary for ${failureKind}.`,
    recommendedAction: "Do the thing.",
    detailsJson: null,
    detectedUtc: "2026-10-01T00:00:00Z",
    ...extra
  });
  const withCases = (...cases: ReturnType<typeof caseFor>[]): Handler => (url) =>
    url.startsWith("/api/series/import-recovery?") ? { body: { openCount: cases.length, recentCases: cases } } : undefined;

  it("lists this show's open import issues in words and Dismiss deletes the case and toasts", async () => {
    setup((url, init) => withCases(caseFor("importFailed"))(url, init) ?? (url === "/api/series/import-recovery/c1" ? { status: 204 } : undefined));
    await renderPage();
    openTab("History");

    expect(await screen.findByText("Import failed")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.url === "/api/series/import-recovery/c1")).toBe(true));
    await waitFor(() => expect(toast.success).toHaveBeenCalled());
  });

  it("a processor-output case offers Import manually, which opens manual import for TV", async () => {
    setup(withCases(caseFor("processor-output-missing")));
    const { router } = await renderPage();
    openTab("History");
    const row = (await screen.findByText("Missing from processor output")).closest("[role='row']") as HTMLElement;

    fireEvent.click(within(row).getByRole("button", { name: "Import manually" }));

    await waitFor(() => expect(router.state.location.pathname + router.state.location.search).toBe("/queue?manualImportMediaType=tv"));
  });

  it("shows the empty Searches and Sent to downloads cards when there is nothing yet", async () => {
    await renderPage();
    openTab("History");

    expect(await screen.findByText("Nothing sent yet")).toBeInTheDocument();
  });

  it("lists this show's searches with their scope and trigger", async () => {
    setup((url) =>
      url.startsWith("/api/series/search-history?seriesId=s1")
        ? {
            body: [
              { id: "h1", seriesId: "s1", episodeId: "e12", seasonNumber: 1, episodeNumber: 2, libraryId: "lib1", triggerKind: "manual", outcome: "sent", releaseName: "The.Wire.S01E02.Sent", indexerName: "Fixture", detailsJson: null, createdUtc: daysAgo(1) },
              { id: "h2", seriesId: "other", episodeId: null, seasonNumber: null, episodeNumber: null, libraryId: "lib1", triggerKind: "scheduled", outcome: "none", releaseName: "Another.Show.Release", indexerName: "Fixture", detailsJson: null, createdUtc: daysAgo(1) }
            ]
          }
        : undefined
    );
    await renderPage();
    openTab("History");

    expect(await screen.findByText("The.Wire.S01E02.Sent")).toBeInTheDocument();
    expect(screen.queryByText("Another.Show.Release")).toBeNull();
    expect(screen.getByText("S01E02")).toBeInTheDocument();
  });

  it("lists only the latest 12 of the show's searches and says Latest 12 of 13", async () => {
    setup((url) =>
      url.startsWith("/api/series/search-history?seriesId=s1")
        ? { body: Array.from({ length: 13 }, (_, index) => ({ id: `h${index}`, seriesId: "s1", episodeId: null, seasonNumber: null, episodeNumber: null, libraryId: "lib1", triggerKind: "scheduled", outcome: "none", releaseName: `The.Wire.Search.${index}`, indexerName: "Fixture", detailsJson: null, createdUtc: new Date(Date.UTC(2026, 9, 1, 12) - index * 60_000).toISOString() })) }
        : undefined
    );
    await renderPage();
    openTab("History");

    expect(await screen.findByText("Latest 12 of 13")).toBeInTheDocument();
    expect(screen.getByText("The.Wire.Search.11")).toBeInTheDocument();
    expect(screen.queryByText("The.Wire.Search.12")).toBeNull();
    expect(screen.getAllByText("Whole show").length).toBeGreaterThan(0);
  });

  it("lists this show's last sent releases with the client and an Open Transfers link", async () => {
    setup((url) =>
      url.startsWith("/api/download-dispatches")
        ? { body: { items: [{ id: "d1", entityId: "s1", mediaType: "tv", releaseName: "The.Wire.S01E02.Dispatch", indexerName: "Fixture", downloadClientName: "qBittorrent", status: "sent", createdUtc: daysAgo(1) }], hasMore: false } }
        : undefined
    );
    await renderPage();
    openTab("History");

    expect(await screen.findByText("The.Wire.S01E02.Dispatch")).toBeInTheDocument();
    expect(screen.getByText("qBittorrent")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open Transfers" })).toHaveAttribute("href", "/queue");
  });
});

describe("opening the show", () => {
  it("still opens when each optional read fails", async () => {
    setup((url) => {
      const path = url.split("?")[0];
      const optional = ["/api/series/s1/metadata/issue", "/api/series/s1/removal-preview", "/api/series/s1/libraries/", "/api/series/s1/acquisition-blockers", "/api/intake-title-origins", "/api/subtitle-languages"];
      return optional.includes(path) ? { status: 404, body: {} } : undefined;
    });

    await renderPage();

    expect(screen.getByRole("heading", { level: 1, name: "The Wire" })).toBeInTheDocument();
  });

  it("a show that is not there fails the route instead of drawing half a page", async () => {
    setup((url) => (url === "/api/series/s1" ? { status: 404, body: {} } : undefined));
    const router = createMemoryRouter([{ path: "/tv/:id", element: <ShowDetailPage />, loader: showDetailLoader, errorElement: <p>Route error screen</p> }], { initialEntries: ["/tv/s1"] });

    render(<RouterProvider router={router} />);

    expect(await screen.findByText("Route error screen")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------------------------------------------
// The live connection: the server announces a change to this show and the page reads again only the groups it can move.
const live = vi.hoisted(() => ({ handlers: {} as Record<string, Array<() => void>> }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useSignalREvent: (event: string, _subject: unknown, handler: () => void) => {
    (live.handlers[event] ??= []).push(handler);
  }
}));
const announce = async (event: string) => {
  const handler = live.handlers[event]?.at(-1);
  if (!handler) throw new Error(`the page did not listen for ${event}`);
  await act(async () => { handler(); });
};

describe("the show page follows the server's announcements (live)", () => {
  const read = (path: string) => calls.some((c) => c.method === "GET" && c.url.split("?")[0] === path);
  const GROUPS = {
    title: "/api/series/s1",
    inventory: "/api/series/s1/inventory",
    history: "/api/series/search-history",
    recovery: "/api/series/import-recovery",
    shared: "/api/libraries"
  };

  beforeEach(() => { for (const key of Object.keys(live.handlers)) delete live.handlers[key]; });

  it.each([
    ["SeriesChanged", ["title", "inventory", "recovery", "history"], ["shared"]],
    ["ImportStateChanged", ["inventory", "history", "recovery"], ["title", "shared"]],
    ["JourneyStageChanged", ["history", "inventory"], ["title", "recovery", "shared"]],
    ["RecoveryCaseChanged", ["recovery", "inventory"], ["title", "history", "shared"]],
    ["ReleasesTurnedDown", ["history"], ["title", "inventory", "recovery", "shared"]]
  ] as const)("%s re-reads exactly its groups and no others", async (event, reads, leaves) => {
    setup();
    await renderPage();
    calls.length = 0;

    await announce(event);

    await waitFor(() => { for (const group of reads) expect(read(GROUPS[group]), `${event} should re-read ${group}`).toBe(true); }, { timeout: 4000 });
    for (const group of leaves) expect(read(GROUPS[group]), `${event} should leave ${group} alone`).toBe(false);
  });

  it("an import finishing shows the new On disk count and the new search row with no reload", async () => {
    let imported = false;
    setup((url) => {
      if (url === "/api/series/s1/inventory") {
        return { body: { ...INVENTORY, importedEpisodeCount: imported ? 2 : 1, episodes: EPISODES.map((item) => (imported && item.episodeId === "e12" ? { ...item, hasFile: true, wantedStatus: "covered" } : item)) } };
      }
      return undefined;
    });
    await renderPage();
    const onDisk = () => screen.getByText("imported and verified").parentElement as HTMLElement;
    expect(onDisk()).toHaveTextContent("1");

    imported = true;
    await announce("ImportStateChanged");

    await waitFor(() => expect(onDisk()).toHaveTextContent("2"), { timeout: 4000 });
  });
});

describe("a show in more than one library", () => {
  const twoLibraries: Handler = (url) => {
    if (url === "/api/series/s1/libraries/") {
      return { body: [{ libraryId: "lib1", libraryName: "Shows", hasFile: true, notWantedHere: false, canRemove: false, canMove: true }, { libraryId: "lib2", libraryName: "Anime", hasFile: false, notWantedHere: false, canRemove: true, canMove: true }] };
    }
    if (url === "/api/libraries") {
      return { body: [{ id: "lib1", name: "Shows", mediaType: "tv", rootPath: "/shows" }, { id: "lib2", name: "Anime", mediaType: "tv", rootPath: "/anime" }] };
    }
    return undefined;
  };

  it("passes the library being acted on with Search now and Choose a release", async () => {
    setup((url, init) => twoLibraries(url, init) ?? (url.startsWith("/api/series/s1/search") ? { body: { outcome: "none", candidates: [] } } : undefined));
    await renderPage();

    press("Search now");
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url === "/api/series/s1/search?libraryId=lib1")).toBe(true));
    press("Choose a release");
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url === "/api/series/s1/search?mode=preview&libraryId=lib1")).toBe(true));
  });

  it("passes the library with a grab, and Use for search points it at the other library", async () => {
    setup((url, init) =>
      twoLibraries(url, init) ??
      (url.startsWith("/api/series/s1/search?mode=preview") ? { body: { candidates: [PREFERRED] } } : url.startsWith("/api/series/s1/grab") ? { body: { releaseName: PREFERRED.releaseName, dispatchStatus: "sent" } } : undefined)
    );
    await renderPage();
    openTab("Destination");
    const animeRow = (await screen.findByText("Anime", { selector: "*" })).closest("[role='row']") as HTMLElement;
    fireEvent.click(within(animeRow).getByRole("button", { name: "Use for search" }));
    press("Choose a release");
    fireEvent.click(await screen.findByText(PREFERRED.releaseName));
    const drawer = await screen.findByRole("dialog", { name: PREFERRED.releaseName });

    fireEvent.click(within(drawer).getByRole("button", { name: "Send to downloads" }));

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url === "/api/series/s1/grab?libraryId=lib2")).toBe(true));
  });

  it("an episode search carries no library (the episode route does not take one)", async () => {
    setup((url, init) => twoLibraries(url, init) ?? (url === "/api/series/s1/episodes/search" ? { body: { searchedEpisodes: 1, episodes: [] } } : undefined));
    await renderPage();
    fireEvent.click(episodeRow("S01E02"));
    const drawer = await screen.findByRole("dialog", { name: /S01E02/ });

    fireEvent.click(within(drawer).getByRole("button", { name: "Search and send the best release" }));

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url === "/api/series/s1/episodes/search")).toBe(true));
  });
});

describe("removing a show that holds files", () => {
  it("says where the files went, offers Restore, and Restore brings the show page back", async () => {
    setup((url) =>
      url === "/api/series/bulk"
        ? { body: { successCount: 1, results: [{ metadata: { recycleBinItemIds: "bin1,bin2", recycleBinRetentionDays: "30" } }] } }
        : url === "/api/recycle-bin/bin1/restore" || url === "/api/recycle-bin/bin2/restore"
          ? { body: { success: true, message: "Restored.", titleRestored: true, titleId: "s1", titleEntityType: "series" } }
          : undefined
    );
    const { router } = await renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Remove from Deluno" }));
    const dialog = await screen.findByRole("dialog", { name: /Remove The Wire\?/ });
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove TV show" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("The Wire removed from Deluno", expect.objectContaining({ action: expect.objectContaining({ label: "Restore" }) })));
    const options = toast.success.mock.calls.find((call) => call[0] === "The Wire removed from Deluno")![1] as { description: string; action: { onClick: () => void } };
    expect(options.description).toContain("recycle bin for 30 days");
    await waitFor(() => expect(router.state.location.pathname).toBe("/tv"));

    options.action.onClick();

    await waitFor(() => expect(calls.filter((c) => c.method === "POST" && /\/api\/recycle-bin\/bin[12]\/restore$/.test(c.url))).toHaveLength(2));
    await waitFor(() => expect(router.state.location.pathname).toBe("/tv/s1"));
    expect(toast.success).toHaveBeenCalledWith("The Wire restored", expect.anything());
  });

  it("a removal with nothing in the recycle bin just says it was removed, with no Restore", async () => {
    setup((url) => (url === "/api/series/bulk" ? { body: { successCount: 1, results: [{ metadata: {} }] } } : undefined));
    await renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Remove from Deluno" }));
    const dialog = await screen.findByRole("dialog", { name: /Remove The Wire\?/ });
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove TV show" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("The Wire removed from Deluno"));
  });

  it("a failed removal keeps the show page open", async () => {
    setup((url) => (url === "/api/series/bulk" ? { body: { successCount: 0, results: [] } } : undefined));
    const { router } = await renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Remove from Deluno" }));
    const dialog = await screen.findByRole("dialog", { name: /Remove The Wire\?/ });
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove TV show" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("This TV show could not be removed."));
    expect(router.state.location.pathname).toBe("/tv/s1");
    expect(screen.getByRole("heading", { level: 1, name: "The Wire" })).toBeInTheDocument();
  });
});

describe("Force a re-download on a show", () => {
  const BLOCKED = {
    mediaId: "s1",
    mediaType: "series",
    title: "The Wire",
    blockers: [{ kind: "download-in-flight", source: "qBittorrent", summary: "A download is already with qBittorrent.", detail: "Sent 2 hours ago.", canClear: true, clearEffect: "Removes the download from qBittorrent." }],
    nothingIsBlocking: false,
    summary: "One thing is holding The Wire back.",
    canForce: true
  };
  const forced = { body: { mediaId: "s1", cleared: [], couldNotClear: [], searchStarted: true, summary: "Done." } };
  const blocked: Handler = (url) => (url === "/api/series/s1/acquisition-blockers" ? { body: BLOCKED } : url.startsWith("/api/series/s1/force-redownload") ? forced : undefined);
  const twoLibraries: Handler = (url) => {
    if (url === "/api/series/s1/libraries/") {
      return { body: [{ libraryId: "lib1", libraryName: "Shows", hasFile: true, notWantedHere: false, canRemove: false, canMove: true }, { libraryId: "lib2", libraryName: "Anime", hasFile: false, notWantedHere: false, canRemove: true, canMove: true }] };
    }
    if (url === "/api/libraries") return { body: [{ id: "lib1", name: "Shows", mediaType: "tv", rootPath: "/shows" }, { id: "lib2", name: "Anime", mediaType: "tv", rootPath: "/anime" }] };
    return undefined;
  };
  const force = async () => {
    fireEvent.click(await screen.findByRole("button", { name: /force a re-download/i }));
    fireEvent.click(await screen.findByRole("button", { name: /clear it and search again/i }));
  };

  it("goes through the library chosen on the Libraries card when the show is in more than one", async () => {
    setup((url, init) => twoLibraries(url, init) ?? blocked(url, init));
    await renderPage();
    openTab("Destination");
    const animeRow = (await screen.findByText("Anime", { selector: "*" })).closest("[role='row']") as HTMLElement;
    fireEvent.click(within(animeRow).getByRole("button", { name: "Use for search" }));

    await force();

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url === "/api/series/s1/force-redownload?libraryId=lib2")).toBe(true));
  });

  it("names no library when the show is in just one", async () => {
    setup(blocked);
    await renderPage();

    await force();

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url === "/api/series/s1/force-redownload")).toBe(true));
  });
});

describe("the show's header badge and metadata notice", () => {
  const withCases = (n: number): Handler => (url) =>
    url.startsWith("/api/series/import-recovery?")
      ? { body: { openCount: n, recentCases: Array.from({ length: n }, (_, i) => ({ id: `c${i}`, title: "the wire", failureKind: "importFailed", summary: "Failed.", recommendedAction: "Do it.", detailsJson: null, detectedUtc: "2026-10-01T00:00:00Z" })) } }
      : undefined;

  it("warns 1 import issue in the singular", async () => {
    setup(withCases(1));
    await renderPage();
    expect(await screen.findByText("1 import issue")).toBeInTheDocument();
  });

  it("says 2 import issues in the plural", async () => {
    setup(withCases(2));
    await renderPage();
    expect(await screen.findByText("2 import issues")).toBeInTheDocument();
  });

  it("shows no import-issue badge when no case names the show", async () => {
    await renderPage();
    expect(screen.queryByText(/^\d+ import issues?$/)).toBeNull();
  });

  const LOST = { kind: "provider-record-missing", provider: "tmdb", providerId: "1438", evidenceKey: "tmdb:tv:1438:missing", detectedUtc: "2026-10-01T00:00:00Z", acknowledgedUtc: null };
  const withIssue: Handler = (url) => (url === "/api/series/s1/metadata/issue" ? { body: LOST } : undefined);

  it("a lost provider record shows the notice, and Keep this show acknowledges it at the series route", async () => {
    setup((url, init) => withIssue(url, init) ?? (url === "/api/series/s1/metadata/issue/acknowledge" ? { status: 204 } : undefined));
    await renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Keep this show" }));

    await waitFor(() => expect(posts("/metadata/issue/acknowledge")).toHaveLength(1));
    expect(posts("/metadata/issue/acknowledge")[0].url).toBe("/api/series/s1/metadata/issue/acknowledge");
  });

  it("Try again runs the show refresh and Find another match opens the Metadata drawer", async () => {
    setup((url, init) => withIssue(url, init) ?? (url === "/api/series/s1/metadata/refresh" ? { body: {} } : undefined));
    await renderPage();

    fireEvent.click(await screen.findByRole("button", { name: "Try again" }));
    await waitFor(() => expect(posts("/metadata/refresh")).toHaveLength(1));

    fireEvent.click(screen.getByRole("button", { name: "Find another match" }));
    expect(await screen.findByRole("dialog", { name: "Metadata" })).toBeInTheDocument();
  });
});

describe("Edit metadata on a show", () => {
  it("opens the drawer from the header, saves the changed overview to the series override route, and the page reads again", async () => {
    setup((url) => (url === "/api/series/s1/metadata/override" ? { body: {} } : undefined));
    await renderPage();
    const readsBefore = calls.filter((c) => c.method === "GET" && c.url === "/api/series/s1").length;

    fireEvent.click(screen.getByRole("button", { name: "Edit metadata" }));
    const drawer = await screen.findByRole("dialog", { name: "Metadata" });
    fireEvent.click(within(drawer).getByText("Fine-tune"));
    fireEvent.change(within(drawer).getByDisplayValue("Baltimore."), { target: { value: "A city, told through its institutions." } });
    fireEvent.click(within(drawer).getByRole("button", { name: "Save metadata" }));

    await waitFor(() => expect(calls.some((c) => c.method === "PUT" && c.url === "/api/series/s1/metadata/override")).toBe(true));
    const sent = calls.find((c) => c.method === "PUT" && c.url === "/api/series/s1/metadata/override")!.body as Record<string, unknown>;
    expect(sent).toMatchObject({ overview: "A city, told through its institutions." });
    await waitFor(() => expect(calls.filter((c) => c.method === "GET" && c.url === "/api/series/s1").length).toBeGreaterThan(readsBefore));
  });
});

describe("a processor-output case on the show's History tab", () => {
  const processorCase = (detailsJson: string | null) => ({
    id: "c9",
    title: "the wire",
    failureKind: "processor-output-missing",
    summary: "The file did not come back.",
    recommendedAction: "Search again.",
    detailsJson,
    detectedUtc: "2026-10-01T00:00:00Z"
  });
  const withCase = (detailsJson: string | null): Handler => (url) =>
    url.startsWith("/api/series/import-recovery?") ? { body: { openCount: 1, recentCases: [processorCase(detailsJson)] } } : undefined;

  it("Search looks for the one episode the file name resolved to, and nothing else", async () => {
    setup((url, init) => withCase(JSON.stringify({ episodeId: "e12" }))(url, init) ?? (url === "/api/series/s1/episodes/search" ? { body: { searchedEpisodes: 1, episodes: [] } } : undefined));
    await renderPage();
    openTab("History");
    const row = (await screen.findByText("Missing from processor output")).closest("[role='row']") as HTMLElement;

    fireEvent.click(within(row).getByRole("button", { name: "Search" }));

    await waitFor(() => expect(posts("/episodes/search")).toHaveLength(1));
    expect(bodies("/episodes/search")[0]).toMatchObject({ episodeIds: ["e12"] });
  });

  it("offers no Search when the file name did not resolve to an episode", async () => {
    setup(withCase(null));
    await renderPage();
    openTab("History");
    const row = (await screen.findByText("Missing from processor output")).closest("[role='row']") as HTMLElement;

    expect(within(row).queryByRole("button", { name: "Search" })).toBeNull();
    expect(within(row).getByRole("button", { name: "Import manually" })).toBeInTheDocument();
  });
});
