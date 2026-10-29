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

async function renderPage(heading = "The Wire") {
  const router = createMemoryRouter(
    [
      { path: "/tv/:id", element: <ShowDetailPage />, loader: showDetailLoader },
      { path: "/tv", element: <p>All the shows</p> },
      { path: "*", element: <p>elsewhere</p> }
    ],
    { initialEntries: ["/tv/s1"] }
  );
  render(<RouterProvider router={router} />);
  await screen.findByRole("heading", { level: 1, name: heading });
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


beforeEach(() => window.localStorage.clear());

/** Holds the answer to every search call until the test lets it go. */
function holdSearches() {
  let release!: () => void;
  const held = new Promise<void>((resolve) => (release = resolve));
  const inner = authedFetch.getMockImplementation()!;
  authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.startsWith("/api/series/s1/search") || url.startsWith("/api/series/s1/episodes/search")) await held;
    return inner(input, init);
  });
  return release;
}

describe("searching a show: buttons, note and empty history (low rows)", () => {
  it("Search now shows a spinner on itself and disables the other actions while it runs, then the note says what the search found", async () => {
    setup((url) => (url === "/api/series/s1/search" ? { body: { outcome: "none", reason: "no_indexers" } } : undefined));
    const release = holdSearches();
    await renderPage();
    const searcher = screen.getAllByRole("button", { name: "Search now" })[0];
    const chooser = screen.getAllByRole("button", { name: "Choose a release" })[0];
    expect(screen.queryByRole("status", { name: "Result of the last search" })).toBeNull();

    fireEvent.click(searcher);

    await waitFor(() => expect(searcher).toBeDisabled());
    expect(chooser).toBeDisabled();
    expect(searcher.querySelector(".animate-spin")).not.toBeNull();
    expect(chooser.querySelector(".animate-spin")).toBeNull();
    expect(screen.getByRole("status", { name: "Result of the last search" })).toHaveTextContent("Searching your indexers...");

    release();
    await waitFor(() => expect(searcher).toBeEnabled());
    const note = screen.getByRole("status", { name: "Result of the last search" });
    expect(note).toHaveTextContent("No indexers are linked to this library");
    expect(note).not.toHaveTextContent("Searching your indexers...");
  });

  it("a search that cannot run leaves an error note on the page", async () => {
    setup((url) => (url === "/api/series/s1/search" ? { status: 400, body: {} } : undefined));
    await renderPage();

    press("Search now");

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(screen.getByRole("status", { name: "Result of the last search" })).toBeInTheDocument();
    expect(screen.getByRole("status", { name: "Result of the last search" })).not.toHaveTextContent("Searching your indexers...");
  });

  it("Search and send these shows a spinner while it runs", async () => {
    setup((url) => (url === "/api/series/s1/episodes/search" ? { body: { searchedEpisodes: 2, episodes: [] } } : undefined));
    const release = holdSearches();
    await renderPage();
    const button = screen.getByRole("button", { name: "Search and send these" });
    expect(button).toBeEnabled();
    expect(button.querySelector(".animate-spin")).toBeNull();

    fireEvent.click(button);

    await waitFor(() => expect(button).toBeDisabled());
    expect(button.querySelector(".animate-spin")).not.toBeNull();
    release();
    await waitFor(() => expect(button).toBeEnabled());
  });

  it("the History tab's empty Searches card says No searches yet, and names the episode once one is picked", async () => {
    await renderPage();
    openTab("History");
    expect(await screen.findByText("No searches yet")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("combobox", { name: /episode/i }));
    fireEvent.click(await screen.findByRole("option", { name: /S01E02/ }));

    expect(await screen.findByText("No searches yet for S01E02")).toBeInTheDocument();
    expect(screen.getByText("Manual and scheduled searches for this episode appear here with their outcomes and explanations.")).toBeInTheDocument();
  });
});

describe("the show's Destination tab, header facts and Next step card (low rows)", () => {
  it("the Next step card reads Choose a release after a preview, and its Review candidates button returns to the Episodes tab", async () => {
    setup((url) => (url.startsWith("/api/series/s1/search?mode=preview") ? { body: { outcome: "held", candidates: [PREFERRED, ELIGIBLE] } } : undefined));
    await renderPage();
    press("Choose a release");
    await screen.findByText(PREFERRED.releaseName);
    openTab("Destination");
    const review = await screen.findByRole("button", { name: "Review candidates" });
    expect(within(review.closest("[role='row']") as HTMLElement).getByText("Choose a release")).toBeInTheDocument();

    fireEvent.click(review);

    await waitFor(() => expect(screen.getByRole("radio", { name: "Episodes" })).toBeChecked());
    expect(screen.getByText(PREFERRED.releaseName)).toBeInTheDocument();
  });

  it("Routing and destination names the library, root folder, downloads folder, import workflow and both qualities, with the target falling back to WEB 1080p", async () => {
    setup((url) => (url === "/api/libraries" ? { body: [{ id: "lib1", name: "Shows", mediaType: "tv", rootPath: "/shows", downloadsPath: "/downloads/tv", importWorkflow: "standard" }] } : undefined));
    await renderPage();
    openTab("Destination");
    const heading = await screen.findByText(/^Routing and destination$/i);
    const card = heading.closest("section") ?? heading.parentElement!.parentElement!.parentElement!;
    const text = (label: string) => (within(card as HTMLElement).getByText(label).closest("[role='row']") as HTMLElement).textContent ?? "";

    expect(text("Library")).toContain("Shows");
    expect(text("Root folder")).toContain("/shows");
    expect(text("Downloads folder")).toContain("/downloads/tv");
    expect(text("Import workflow")).toContain("Import immediately");
    expect(text("Current quality")).toContain("Unknown");
    expect(text("Target quality")).toMatch(/WEB 1080p/);
  });

  it("Indexers and download clients marks a tagged connection Not used for an untagged show and an untagged one Used, with the reason", async () => {
    setup((url) => {
      if (url === "/api/libraries/lib1/routing") return { body: { sources: [{ indexerId: "i1", indexerName: "Kids Indexer" }, { indexerId: "i2", indexerName: "Open Indexer" }], downloadClients: [{ downloadClientId: "c1", downloadClientName: "qBittorrent" }] } };
      if (url === "/api/indexers") return { body: [{ id: "i1", name: "Kids Indexer", tags: "kids" }, { id: "i2", name: "Open Indexer", tags: "" }] };
      if (url === "/api/download-clients") return { body: [{ id: "c1", name: "qBittorrent", protocol: "qbittorrent", tags: "" }] };
      if (url === "/api/series/s1/tags") return { body: [] };
      return undefined;
    });
    await renderPage();
    openTab("Destination");
    const rowOf = async (name: string) => (await screen.findByText(name, { selector: "*" })).closest("[role='row']") as HTMLElement;

    expect(within(await rowOf("Kids Indexer")).getByText("Not used")).toBeInTheDocument();
    expect(within(await rowOf("Open Indexer")).getByText("Used")).toBeInTheDocument();
    expect(within(await rowOf("qBittorrent")).getByText("Used")).toBeInTheDocument();
    expect(screen.getByText("2 of 3 used")).toBeInTheDocument();
  });

  it("How this show was added lists each import list and says removing the list never removes the show or its files", async () => {
    setup((url) => (url.startsWith("/api/intake-title-origins") ? { body: [{ id: "o1", sourceId: "src1", sourceName: "Trakt watchlist", provider: "trakt", mediaType: "tv", entityId: "s1", entryKey: "k", title: "The Wire", year: 2002, imdbId: null, firstSeenUtc: "2026-09-01T00:00:00Z", lastSeenUtc: "2026-10-01T00:00:00Z" }] } : undefined));
    await renderPage();
    openTab("Destination");

    expect(await screen.findByText(/^How this show was added$/i)).toBeInTheDocument();
    expect(screen.getByText("1 import list")).toBeInTheDocument();
    expect(screen.getByText("Trakt watchlist")).toBeInTheDocument();
    expect(screen.getByText("Removing the list never removes this show or its files.")).toBeInTheDocument();
  });

  it("the facts grid reads Episodes held of aired, the network, studio, language, director, status, the numbering words and the import issues, and leaves out what it does not know", async () => {
    setup((url) => (url === "/api/series/s1" ? { body: { ...SERIES, metadataJson: JSON.stringify({ Network: "HBO", Studio: "Blown Deadline", OriginalLanguage: "English", Director: "Ed Bianchi", Status: "Ended" }) } } : undefined));
    await renderPage();

    const facts = (label: string) => screen.getByText(label, { selector: "dt" }).nextElementSibling?.textContent;
    expect(facts("Episodes")).toBe("1/3 aired held");
    expect(facts("Network")).toBe("HBO");
    expect(facts("Studio")).toBe("Blown Deadline");
    expect(facts("Language")).toBe("English");
    expect(facts("Director")).toBe("Ed Bianchi");
    expect(facts("Status")).toBe("Ended");
    expect(facts("Numbering")).toBe("Standard · Season / episode");
    expect(facts("Added")).toBeTruthy();
    expect(screen.queryByText("Collection", { selector: "dt" })).toBeNull();
    expect(screen.queryByText("Import issues", { selector: "dt" })).toBeNull();
  });
});
