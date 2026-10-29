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

import { activityLinkForTitle } from "../lib/activity-history";
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

const search = (over: Record<string, unknown>) => ({ id: "h1", seriesId: "s1", episodeId: null, seasonNumber: null, episodeNumber: null, libraryId: "lib1", triggerKind: "manual", outcome: "sent", releaseName: "The.Wire.Release", indexerName: "Fixture", detailsJson: null, createdUtc: daysAgo(1), ...over });
const searchesReply = (...items: unknown[]): Handler => (url) => (url.startsWith("/api/series/search-history?seriesId=s1") ? { body: items } : undefined);

describe("the show page navigation and Specials wording (low rows)", () => {
  it("the All TV button on the show page returns to /tv", async () => {
    const { router } = await renderPage();

    fireEvent.click(screen.getByRole("link", { name: /All TV/ }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/tv"));
  });

  it("Season 0 reads Specials in the season switch label and in the History scope", async () => {
    setup(searchesReply(search({ id: "h0", seasonNumber: 0, releaseName: "The.Wire.Specials.Pack" })));
    await renderPage();

    expect(screen.getAllByText("Specials").length).toBeGreaterThan(0);
    expect(screen.getByRole("switch", { name: "Monitor Specials" })).toBeInTheDocument();
    openTab("History");
    const row = (await screen.findByText("The.Wire.Specials.Pack")).closest("[role='row']") as HTMLElement;
    expect(within(row).getByText("Specials")).toBeInTheDocument();
  });
});

describe("the show's Choose a release candidates (low rows)", () => {
  const cand = (over: Record<string, unknown>) => ({ ...PREFERRED, ...over });
  const A = cand({ releaseName: "A.Release.Torrent", indexerName: "Alpha Indexer", indexerProtocol: "torznab", sizeBytes: 3_000_000_000, seeders: 5, decisionStatus: "preferred" });
  const B = cand({ releaseName: "B.Release.Usenet", indexerName: "Beta Indexer", indexerProtocol: "newznab", sizeBytes: 1_000_000_000, seeders: null, decisionStatus: "eligible" });
  const C = cand({ releaseName: "C.Release.Unknown", indexerName: "Alpha Indexer", indexerProtocol: "torznab", sizeBytes: null, seeders: 90, decisionStatus: "rejected", meetsCutoff: false, decisionReasons: ["Rejected by the typed release plan."] });
  const preview = (url: string) => (url.startsWith("/api/series/s1/search?mode=preview") ? { body: { outcome: "held", candidates: [A, B, C] } } : undefined);
  const order = () => screen.getAllByText(/^[ABC]\.Release\./).map((n) => n.textContent);

  async function choose() {
    setup(preview);
    await renderPage();
    press("Choose a release");
    await screen.findByText(A.releaseName);
  }
  const header = (label: string) => screen.getAllByRole("columnheader").find((h) => h.textContent === label) as HTMLElement;

  it("shows the server's ranking until a header is clicked, then sorts by it and puts an unknown value last either way", async () => {
    await choose();
    expect(order()).toEqual(["A.Release.Torrent", "B.Release.Usenet", "C.Release.Unknown"]);

    fireEvent.click(within(header("Size")).getByRole("button"));
    expect(order()).toEqual(["B.Release.Usenet", "A.Release.Torrent", "C.Release.Unknown"]);
    fireEvent.click(within(header("Size")).getByRole("button"));
    expect(order()).toEqual(["A.Release.Torrent", "B.Release.Usenet", "C.Release.Unknown"]);

    fireEvent.click(within(header("Seeders/Leechers")).getByRole("button"));
    expect(order()).toEqual(["A.Release.Torrent", "C.Release.Unknown", "B.Release.Usenet"]);
    fireEvent.click(within(header("Seeders/Leechers")).getByRole("button"));
    expect(order()).toEqual(["C.Release.Unknown", "A.Release.Torrent", "B.Release.Usenet"]);
  });

  it("the Protocol and Indexer filter menus narrow the list and the count reads 'N of M candidates'", async () => {
    await choose();
    expect(screen.getByText("3 of 3 candidates")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("combobox", { name: "Filter by indexer" }));
    fireEvent.click(screen.getByRole("option", { name: "Beta Indexer" }));
    expect(order()).toEqual(["B.Release.Usenet"]);
    expect(screen.getByText("1 of 3 candidates")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("combobox", { name: "Filter by indexer" }));
    fireEvent.click(screen.getByRole("option", { name: "Any indexer" }));
    fireEvent.click(screen.getByRole("combobox", { name: "Filter by protocol" }));
    fireEvent.click(screen.getAllByRole("option").find((item) => /torrent/i.test(item.textContent ?? "")) as HTMLElement);
    expect(order()).toEqual(["A.Release.Torrent", "C.Release.Unknown"]);
    expect(screen.getByText("2 of 3 candidates")).toBeInTheDocument();
  });

  it("a rejected release shows its first reason in plain words under the chip", async () => {
    await choose();
    const row = screen.getByText(C.releaseName).closest("[role='row']") as HTMLElement;
    expect(within(row).getByText("Rejected")).toBeInTheDocument();
    expect(within(row).getByText("Not taken.")).toBeInTheDocument();
    expect(within(row).queryByText(/typed release plan/)).toBeNull();
  });

  it("the release drawer lists how Deluno evaluated it: quality, who judged, cutoff, size and seeders", async () => {
    await choose();
    fireEvent.click(screen.getByText(A.releaseName));
    const drawer = await screen.findByRole("dialog", { name: A.releaseName });

    expect(within(drawer).getByText("How Deluno evaluated it")).toBeInTheDocument();
    for (const [label, value] of [["Quality", "WEB 1080p"], ["Judged by", "Your older rules"], ["Meets cutoff", "Yes"], ["Size", "2.8 GB"], ["Seeders", "5"]]) {
      const fact = within(drawer).getByText(label).parentElement as HTMLElement;
      expect(fact.textContent).toContain(value);
    }
  });
});

describe("the show's History tab (low rows)", () => {
  it("the episode filter menu narrows the Searches to one episode, Every episode clears it and an episode with none says so", async () => {
    setup(searchesReply(
      search({ id: "h1", episodeId: "e12", seasonNumber: 1, episodeNumber: 2, releaseName: "Wire.S01E02.Search" }),
      search({ id: "h2", episodeId: "e11", seasonNumber: 1, episodeNumber: 1, releaseName: "Wire.S01E01.Search" })
    ));
    await renderPage();
    openTab("History");
    await screen.findByText("Wire.S01E02.Search");

    fireEvent.click(screen.getByRole("combobox", { name: "Filter history to episode" }));
    fireEvent.click(screen.getByRole("option", { name: /S01E02/ }));
    expect(screen.getByText("Wire.S01E02.Search")).toBeInTheDocument();
    expect(screen.queryByText("Wire.S01E01.Search")).toBeNull();

    fireEvent.click(screen.getByRole("combobox", { name: "Filter history to episode" }));
    fireEvent.click(screen.getByRole("option", { name: /S01E03/ }));
    expect(await screen.findByText("No searches yet for S01E03")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("combobox", { name: "Filter history to episode" }));
    fireEvent.click(screen.getByRole("option", { name: "Every episode" }));
    expect(screen.getByText("Wire.S01E01.Search")).toBeInTheDocument();
  });

  it("a search row opens the Search drawer with Outcome, Trigger, Scope, Source, Release and the release outcomes", async () => {
    const details = JSON.stringify({ candidates: [{ releaseName: "Considered.One.1080p", indexerName: "Fixture", quality: "WEB 1080p", score: 5, meetsCutoff: true, summary: "ok", decisionStatus: "preferred", preferenceEvaluation: { status: "preferred" } }] });
    setup(searchesReply(search({ id: "h1", releaseName: "The.Wire.Sent.Release", detailsJson: details })));
    await renderPage();
    openTab("History");

    fireEvent.click(await screen.findByText("The.Wire.Sent.Release"));
    const drawer = await screen.findByRole("dialog");

    for (const [label, value] of [["Trigger", "Manual"], ["Scope", "Whole show"], ["Source", "Fixture"], ["Release", "The.Wire.Sent.Release"]]) {
      expect((within(drawer).getByText(label).parentElement as HTMLElement).textContent).toContain(value);
    }
    expect(within(drawer).getByText("Release outcomes")).toBeInTheDocument();
    expect(within(drawer).getByText("Considered.One.1080p")).toBeInTheDocument();
  });

  it("the Activity card lists the latest events for the show and links to everything about it", async () => {
    setup((url) => (url.startsWith("/api/activity") ? { body: { items: [{ id: "a1", category: "search", title: "Searched", message: "Searched The Wire and looked at 3 releases", createdUtc: daysAgo(1), relatedEntityType: "series", relatedEntityId: "s1" }], hasMore: false } } : undefined));
    await renderPage();
    openTab("History");

    expect(await screen.findByText("Searched The Wire and looked at 3 releases")).toBeInTheDocument();
    expect(calls.some((c) => c.url.startsWith("/api/activity?relatedEntityType=series&relatedEntityId=s1"))).toBe(true);
    expect(screen.getByRole("link", { name: "Everything about this show" })).toHaveAttribute("href", activityLinkForTitle("The Wire", "series", "s1"));
  });

  it("with no events the Activity card says Nothing has happened yet", async () => {
    await renderPage();
    openTab("History");

    expect(await screen.findByText("Nothing has happened yet")).toBeInTheDocument();
  });

  it("the decision trail is read for this show and listed with its reason", async () => {
    setup((url) => (url.startsWith("/api/decisions") ? { body: { items: [{ id: "d1", occurredUtc: daysAgo(1), scope: "search.series", scopeLabel: "Search for The Wire", status: "completed", reason: "Picked the 1080p web release", inputs: {}, outcome: "Sent it", alternatives: [], relatedJobId: null, relatedEntityType: "series", relatedEntityId: "s1" }], hasMore: false } } : undefined));
    await renderPage();
    openTab("History");

    expect(await screen.findByText("Picked the 1080p web release")).toBeInTheDocument();
    expect(calls.some((c) => c.url.startsWith("/api/decisions?relatedEntityType=series&relatedEntityId=s1"))).toBe(true);
  });
});
