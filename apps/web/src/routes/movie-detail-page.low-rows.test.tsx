import { act, cleanup, configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The workstation can be busy with other builds; a slow render is not a failed page.
configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The film page's own wiring (coverage register, area movie-detail): the page is rendered through its real
 * loader with only the network answered, and each button is pressed the way a person presses it. What the
 * server does with those calls is proven by the backend tests named in the register.
 */
const authedFetch = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch, useAuth: () => ({ token: "t" }) }));

const toast = vi.hoisted(() => {
  const fn = Object.assign(vi.fn(), {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
    message: vi.fn(),
    dismiss: vi.fn()
  });
  return fn;
});
vi.mock("../components/shell/toaster", () => ({ toast, realtimeToast: {}, Toaster: () => null }));

// jsdom has no layout: the synopsis measures itself with matchMedia, and some list cells watch their size.
window.matchMedia ??= ((query: string) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, onchange: null, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;

const { MovieDetailPage, movieDetailLoader } = await import("./movie-detail-page");

type Reply = { status?: number; body?: unknown };
type Handler = (url: string, init?: RequestInit) => Reply | undefined;

const MOVIE = {
  id: "m1",
  title: "Big Buck Bunny",
  releaseYear: 2008,
  imdbId: "tt1254207",
  monitored: true,
  hasFile: false,
  metadataProvider: "tmdb",
  metadataProviderId: "10378",
  originalTitle: null,
  overview: "A rabbit.",
  posterUrl: null,
  backdropUrl: null,
  rating: 7.1,
  genres: "Animation",
  externalUrl: null,
  metadataJson: null,
  metadataUpdatedUtc: null,
  createdUtc: "2026-10-01T00:00:00Z",
  updatedUtc: "2026-10-01T00:00:00Z",
  libraryId: "lib1",
  wantedStatus: "missing",
  wantedReason: "No file yet.",
  targetQuality: "WEB 1080p",
  qualityCutoffMet: false
};

const PREFERRED = {
  releaseName: "Big.Buck.Bunny.2008.1080p.WEB-DL-GRP",
  indexerId: "idx1",
  indexerName: "Fixture Indexer",
  quality: "WEB 1080p",
  score: 10,
  meetsCutoff: true,
  summary: "Best match for this profile.",
  downloadUrl: "https://fixture.invalid/preferred.torrent",
  sizeBytes: 2_147_483_648,
  seeders: 40,
  leechers: 2,
  indexerProtocol: "torznab",
  decisionStatus: "preferred",
  decisionReasons: ["Best match for this profile"],
  riskFlags: [],
  qualityDelta: 1
};

const ELIGIBLE = {
  ...PREFERRED,
  releaseName: "Big.Buck.Bunny.2008.720p.HDTV-OTHER",
  indexerName: "Second Indexer",
  quality: "HDTV 720p",
  summary: "Acceptable, but not Deluno's pick.",
  downloadUrl: "https://fixture.invalid/eligible.torrent",
  decisionStatus: "eligible",
  qualityDelta: 0
};

let calls: Array<{ url: string; method: string; body: unknown }> = [];
let handler: Handler = () => undefined;

/** Everything the loader reads, answered with a plain empty film page; `handler` overrides per test. */
function answer(url: string, init?: RequestInit): Reply {
  const override = handler(url, init);
  if (override) return override;
  const path = url.split("?")[0];
  if (path === "/api/movies/m1") return { body: MOVIE };
  if (path === "/api/movies/search-history") return { body: [] };
  if (path === "/api/movies/import-recovery") return { body: { totalCases: 0, recentCases: [] } };
  if (path === "/api/libraries") return { body: [{ id: "lib1", name: "Films", mediaType: "movies", rootPath: "/films", importWorkflow: "standard" }] };
  if (path === "/api/movies/m1/libraries/") return { body: [{ libraryId: "lib1", libraryName: "Films", hasFile: false, notWantedHere: false }] };
  if (path === "/api/movies/m1/removal-preview") return { body: { filePaths: [], folderPaths: [], warnings: [] } };
  if (path === "/api/download-dispatches" || path === "/api/activity" || path === "/api/decisions") return { body: { items: [], hasMore: false } };
  if (path === "/api/movies/m1/workflow-status") {
    return { body: { wantedStatus: "missing", reason: "No file yet.", currentQuality: null, targetQuality: "WEB 1080p", preventLowerQualityReplacements: false, lastQualityDeltaDecision: null } };
  }
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

async function renderPage() {
  const router = createMemoryRouter(
    [
      { path: "/movies/:id", element: <MovieDetailPage />, loader: movieDetailLoader },
      { path: "/movies", element: <p>All the movies</p> },
      { path: "*", element: <p>elsewhere</p> }
    ],
    { initialEntries: ["/movies/m1"] }
  );
  const view = render(<RouterProvider router={router} />);
  await screen.findByRole("heading", { level: 1, name: "Big Buck Bunny" });
  return { router, view };
}

/** The toolbar's own button: the Next step card repeats "Search now", and the toolbar comes first on the page. */
const press = (name: string | RegExp) => fireEvent.click(screen.getAllByRole("button", { name })[0]);

beforeEach(() => {
  setup();
});
afterEach(() => {
  authedFetch.mockReset();
  vi.clearAllMocks();
});

import { activityLinkForFile, activityLinkForTitle } from "../lib/activity-history";

beforeEach(() => window.localStorage.clear());

const importCase = (id: string, title = "Big Buck Bunny") => ({ id, title, failureKind: "importFailed", summary: `Summary for ${id}.`, recommendedAction: "Do the thing.", detailsJson: null, detectedUtc: "2026-10-01T00:00:00Z" });
const withCases = (...cases: ReturnType<typeof importCase>[]): Handler => (url) =>
  url.startsWith("/api/movies/import-recovery?") ? { body: { openCount: cases.length, recentCases: cases } } : undefined;

describe("the film header and summary strip (low rows)", () => {
  it("the facts grid shows only the facts that have a value, each under its own label", async () => {
    const fact = (label: string) => screen.queryByText(label, { selector: "dt" })?.nextElementSibling?.textContent ?? null;
    const full = {
      ...MOVIE,
      hasFile: true,
      filePath: "/films/Big Buck Bunny (2008)/Big.Buck.Bunny.2008.mkv",
      fileSizeBytes: 2_147_483_648,
      videoCodec: "x265",
      audioCodec: "EAC3",
      releaseGroup: "GRP",
      metadataJson: JSON.stringify({ Studio: "Blender Foundation", OriginalLanguage: "English", Collection: "Open Movies", Director: "Sacha Goedegebure" })
    };
    setup((url) => (url === "/api/movies/m1" ? { body: full } : url.startsWith("/api/movies/import-recovery?") ? withCases(importCase("c1"))(url) : undefined));
    await renderPage();

    expect(fact("Path")).toBe("/films/Big Buck Bunny (2008)/Big.Buck.Bunny.2008.mkv");
    expect(fact("Size")).toBe("2.0 GB");
    expect(fact("Studio")).toBe("Blender Foundation");
    expect(fact("Language")).toBe("English");
    expect(fact("Collection")).toBe("Open Movies");
    expect(fact("Director")).toBe("Sacha Goedegebure");
    expect(fact("Codec")).toBe("x265 · EAC3");
    expect(fact("Release group")).toBe("GRP");
    expect(fact("Added")).toBeTruthy();
    expect(fact("Import issues")).toBe("1");
    cleanup();

    setup();
    await renderPage();
    for (const label of ["Path", "Size", "Studio", "Language", "Collection", "Director", "Codec", "Release group", "Import issues"]) {
      expect(fact(label), `${label} is left out when the film has no value for it`).toBeNull();
    }
  });

  it("shows an 'N import issue(s)' warning badge in the header when recovery cases name this film, and none otherwise", async () => {
    setup(withCases(importCase("c1"), importCase("c2")));
    await renderPage();
    expect(screen.getByText("2 import issues")).toBeInTheDocument();
    cleanup();

    setup(withCases(importCase("c1")));
    await renderPage();
    expect(screen.getByText("1 import issue")).toBeInTheDocument();
    cleanup();

    setup();
    await renderPage();
    expect(screen.queryByText(/import issues?$/)).toBeNull();
  });

  it("the Import issues summary cell counts the cases and reads 'need a decision', or 'nothing stuck' with none", async () => {
    setup(withCases(importCase("c1"), importCase("c2")));
    await renderPage();
    let cell = screen.getByText("need a decision").parentElement as HTMLElement;
    expect(cell.textContent).toContain("Import issues");
    expect(cell.textContent).toContain("2");
    cleanup();

    setup();
    await renderPage();
    cell = screen.getByText("nothing stuck").parentElement as HTMLElement;
    expect(cell.textContent).toContain("Import issues");
    expect(cell.textContent).toContain("0");
  });

  it.each([
    ["upgrade", "Upgradable"],
    ["covered", "Quality met"]
  ])("a %s film reads %s in the status mark", async (wantedStatus, label) => {
    setup((url) => (url === "/api/movies/m1" ? { body: { ...MOVIE, hasFile: true, wantedStatus } } : undefined));
    await renderPage();

    expect(screen.getAllByText(label).length).toBeGreaterThan(0);
  });

  it("a film the acting library does not want carries unmonitored beside its state in the status mark", async () => {
    setup((url) => {
      if (url === "/api/movies/m1") return { body: { ...MOVIE, hasFile: true, wantedStatus: "upgrade" } };
      if (url === "/api/movies/m1/libraries/") return { body: [{ libraryId: "lib1", libraryName: "Films", hasFile: true, notWantedHere: true, canRemove: true, canMove: true }] };
      return undefined;
    });
    await renderPage();

    expect(document.body.textContent).toMatch(/Upgradable.{0,6}unmonitored/i);
  });

  it("a film with no overview reads the fallback sentence in the synopsis", async () => {
    setup((url) => (url === "/api/movies/m1" ? { body: { ...MOVIE, overview: null } } : undefined));
    await renderPage();

    expect(screen.getByText("No overview has been stored yet. Refresh metadata when you want Deluno to enrich this title.")).toBeInTheDocument();
  });
});

describe("the film's History tab Activity card (low rows)", () => {
  const eventsReply: Handler = (url) =>
    url.startsWith("/api/activity")
      ? { body: { items: Array.from({ length: 11 }, (_, index) => ({ id: `a${index}`, category: "search", title: "Searched", message: `Searched Big Buck Bunny, pass ${index}`, createdUtc: new Date(Date.UTC(2026, 9, 1, 12) - index * 60_000).toISOString(), relatedEntityType: "movie", relatedEntityId: "m1" })), hasMore: false } }
      : undefined;

  it("lists the latest 10 events for the film, read for this film, with the link to everything about it", async () => {
    setup(eventsReply);
    await renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "History" }));

    expect(await screen.findByText("Searched Big Buck Bunny, pass 0")).toBeInTheDocument();
    expect(screen.getByText("Searched Big Buck Bunny, pass 9")).toBeInTheDocument();
    expect(screen.queryByText("Searched Big Buck Bunny, pass 10")).toBeNull();
    expect(calls.some((c) => c.url.startsWith("/api/activity?relatedEntityType=movie&relatedEntityId=m1"))).toBe(true);
    expect(screen.getByRole("link", { name: "Everything about this film" })).toHaveAttribute("href", activityLinkForTitle("Big Buck Bunny", "movie", "m1"));
  });

  it("offers This file's history only when the film has a file, linked to that file's trail", async () => {
    setup((url) => (url === "/api/movies/m1" ? { body: { ...MOVIE, hasFile: true, filePath: "/films/Big Buck Bunny (2008)/Big.Buck.Bunny.2008.mkv" } } : undefined));
    await renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "History" }));
    const link = await screen.findByRole("link", { name: "This file's history" });
    expect(link).toHaveAttribute("href", activityLinkForFile("/films/Big Buck Bunny (2008)/Big.Buck.Bunny.2008.mkv"));
    cleanup();

    setup();
    await renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "History" }));
    expect(await screen.findByRole("link", { name: "Everything about this film" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "This file's history" })).toBeNull();
  });

  it("says Nothing has happened yet when the film has no events", async () => {
    await renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "History" }));

    expect(await screen.findByText("Nothing has happened yet")).toBeInTheDocument();
  });
});

describe("Force a re-download on a film in more than one library (low rows)", () => {
  const BLOCKED = {
    mediaId: "m1",
    mediaType: "movie",
    title: "Big Buck Bunny",
    blockers: [{ kind: "download-in-flight", source: "qBittorrent", summary: "A download is already with qBittorrent.", detail: "Sent 2 hours ago.", canClear: true, clearEffect: "Removes the download from qBittorrent." }],
    nothingIsBlocking: false,
    summary: "One thing is holding Big Buck Bunny back.",
    canForce: true
  };
  const forced = { body: { mediaId: "m1", cleared: [], couldNotClear: [], searchStarted: true, summary: "Done." } };
  const handlers: Handler = (url) => {
    if (url === "/api/movies/m1/acquisition-blockers") return { body: BLOCKED };
    if (url.startsWith("/api/movies/m1/force-redownload")) return forced;
    if (url === "/api/movies/m1/libraries/") {
      return { body: [{ libraryId: "lib1", libraryName: "Films", hasFile: false, notWantedHere: false, canRemove: true, canMove: true }, { libraryId: "lib2", libraryName: "Kids", hasFile: false, notWantedHere: false, canRemove: true, canMove: true }] };
    }
    if (url === "/api/libraries") return { body: [{ id: "lib1", name: "Films", mediaType: "movies", rootPath: "/films" }, { id: "lib2", name: "Kids", mediaType: "movies", rootPath: "/kids" }] };
    return undefined;
  };
  const force = async () => {
    fireEvent.click(await screen.findByRole("button", { name: /force a re-download/i }));
    fireEvent.click(await screen.findByRole("button", { name: /clear it and search again/i }));
  };

  it("passes the acting library to the force and reads the page again afterwards", async () => {
    setup(handlers);
    await renderPage();
    const kidsRow = (await screen.findByText("Kids", { selector: "*" })).closest("[role='row']") as HTMLElement;
    fireEvent.click(within(kidsRow).getByRole("button", { name: "Use for search" }));
    const readsBefore = calls.filter((c) => c.method === "GET" && c.url === "/api/movies/m1").length;

    await force();

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url === "/api/movies/m1/force-redownload?libraryId=lib2")).toBe(true));
    await waitFor(() => expect(calls.filter((c) => c.method === "GET" && c.url === "/api/movies/m1").length).toBeGreaterThan(readsBefore));
  });

  it("disables the Force a re-download button while another action runs", async () => {
    setup(handlers);
    await renderPage();
    expect(await screen.findByRole("button", { name: /force a re-download/i })).not.toBeDisabled();
    const original = authedFetch.getMockImplementation()!;
    authedFetch.mockImplementation((input: RequestInfo | URL, init?: RequestInit) => (String(input).startsWith("/api/movies/m1/search") ? new Promise(() => undefined) : original(input, init)));

    press("Search now");

    await waitFor(() => expect(screen.getByRole("button", { name: /force a re-download/i })).toBeDisabled());
  });

  it("names no library when the film is in just one", async () => {
    setup((url, init) => (url === "/api/movies/m1/libraries/" || url === "/api/libraries" ? undefined : handlers(url, init)));
    await renderPage();

    await force();

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url === "/api/movies/m1/force-redownload")).toBe(true));
  });
});
