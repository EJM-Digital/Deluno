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


const RESPONSES = (candidates: unknown[]): Handler => (url) =>
  url.startsWith("/api/movies/m1/search?mode=preview") ? { body: { outcome: "held", summary: "compared", candidates } } : undefined;
const withMovie = (patch: Record<string, unknown>, extra?: Handler): Handler => (url, init) =>
  url === "/api/movies/m1" ? { body: { ...MOVIE, ...patch } } : extra?.(url, init);

/** The titles of the candidate rows, top to bottom. */
const order = () => screen.getAllByRole("row").map((row) => row.textContent ?? "").filter((text) => text.includes("Big.Buck.Bunny.2008"));

describe("searching a film: the buttons, the Next step card and the candidate list (search-grab low rows)", () => {
  it("while a choose-a-release search runs both search buttons are disabled and only the pressed one shows a spinner", async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    setup(RESPONSES([PREFERRED]));
    const inner = authedFetch.getMockImplementation()!;
    authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).startsWith("/api/movies/m1/search")) await held;
      return inner(input, init);
    });
    await renderPage();
    const chooser = screen.getAllByRole("button", { name: "Choose a release" })[0];
    const searcher = screen.getAllByRole("button", { name: "Search now" })[0];
    expect(chooser.querySelector(".animate-spin")).toBeNull();

    fireEvent.click(chooser);

    await waitFor(() => expect(chooser).toBeDisabled());
    expect(searcher).toBeDisabled();
    expect(chooser.querySelector(".animate-spin")).not.toBeNull();
    expect(searcher.querySelector(".animate-spin")).toBeNull();
    release();
    await waitFor(() => expect(chooser).toBeEnabled());
    expect(searcher).toBeEnabled();
  });

  it("the Next step card reads Choose a release with Review candidates once candidates exist,", async () => {
    setup(RESPONSES([PREFERRED]));
    await renderPage();
    expect(screen.getByText("Find this movie")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Review candidates" })).toBeNull();

    press("Choose a release");
    const review = await screen.findByRole("button", { name: "Review candidates" });
    const card = review.closest("[role='row']") as HTMLElement;
    expect(within(card).getByText("Choose a release")).toBeInTheDocument();
    expect(screen.queryByText("Find this movie")).toBeNull();
  });

  it("the Next step card's Search now on a film below its target runs the automatic search", async () => {
    setup(withMovie({ hasFile: true, currentQuality: "HDTV 720p", qualityCutoffMet: false }, (url) => (url.startsWith("/api/movies/m1/search") ? { body: { outcome: "none", reason: "no_indexers" } } : undefined)));
    await renderPage();
    const card = screen.getByText("Look for an upgrade").closest("[role='row']") as HTMLElement;

    fireEvent.click(within(card).getByRole("button", { name: "Search now" }));

    await waitFor(() => expect(posts("/search")).toHaveLength(1));
    expect(calls.some((c) => c.url.includes("mode=preview"))).toBe(false);
  });

  it("the candidates card says how many are shown of how many were found", async () => {
    setup(RESPONSES([PREFERRED, ELIGIBLE, { ...ELIGIBLE, releaseName: "Big.Buck.Bunny.2008.480p.DVD-X", indexerName: "Third Indexer", indexerProtocol: "newznab" }]));
    await renderPage();
    press("Choose a release");
    await screen.findByText(PREFERRED.releaseName);
    expect(screen.getByText("3 of 3 candidates")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("combobox", { name: "Filter by protocol" }));
    fireEvent.click(await screen.findByRole("option", { name: "Usenet" }));
    expect(await screen.findByText("1 of 3 candidates")).toBeInTheDocument();
  });

  it("a candidate's cells read in plain words: protocol, age, seeders and leechers, grabs and size", async () => {
    setup(RESPONSES([{ ...PREFERRED, indexerProtocol: "torznab", releaseAgeHours: 50, seeders: 40, leechers: 2, grabs: 12, sizeBytes: 2_147_483_648 }, { ...ELIGIBLE, indexerProtocol: "newznab", releaseAgeHours: null, seeders: null, leechers: null, grabs: null, sizeBytes: 0 }]));
    await renderPage();
    press("Choose a release");
    const first = (await screen.findByText(PREFERRED.releaseName)).closest("[role='row']") as HTMLElement;
    const second = screen.getByText(ELIGIBLE.releaseName).closest("[role='row']") as HTMLElement;

    expect(first.textContent).toMatch(/Torrent/);
    expect(first.textContent).toMatch(/2d/);
    expect(first.textContent).toMatch(/40\s*\/\s*2/);
    expect(first.textContent).toMatch(/2(\.0)? GB/);
    expect(first.textContent).toMatch(/12/);
    expect(second.textContent).toMatch(/Usenet/);
    expect(second.textContent).not.toMatch(/undefined|null|NaN/);
  });

  it("clicking a column header sorts the candidates, and the server's ranking holds until then", async () => {
    setup(RESPONSES([{ ...PREFERRED, seeders: 40 }, { ...ELIGIBLE, seeders: 90 }]));
    await renderPage();
    press("Choose a release");
    await screen.findByText(PREFERRED.releaseName);
    expect(order()[0]).toContain(PREFERRED.releaseName);

    fireEvent.click(screen.getByRole("button", { name: /Seeders.Leechers/ }));
    await waitFor(() => expect(screen.getByRole("columnheader", { name: /Seeders.Leechers/ })).toHaveAttribute("aria-sort", "ascending"));
    expect(order()[0]).toContain(PREFERRED.releaseName);
    fireEvent.click(screen.getByRole("button", { name: /Seeders.Leechers/ }));
    await waitFor(() => expect(order()[0]).toContain(ELIGIBLE.releaseName));
  });
});

describe("the candidate table: Decision sort, fresh searches and Force (search-grab low rows, second set)", () => {
  it("clicking the Decision header sorts the candidates by tone, the worst first and then the best first", async () => {
    const REJECTED = { ...ELIGIBLE, releaseName: "Big.Buck.Bunny.2008.480p.DVD-BAD", decisionStatus: "rejected", qualityDelta: -1, meetsCutoff: false };
    const WARN = { ...ELIGIBLE, meetsCutoff: false };
    setup(RESPONSES([WARN, REJECTED, PREFERRED]));
    await renderPage();
    press("Choose a release");
    await screen.findByText(PREFERRED.releaseName);
    // The server order holds until a header is pressed.
    expect(order()[0]).toContain(WARN.releaseName);

    fireEvent.click(screen.getByRole("button", { name: /^Decision/ }));
    await waitFor(() => expect(screen.getByRole("columnheader", { name: /^Decision/ })).toHaveAttribute("aria-sort", "ascending"));
    const ascending = order().map((text) => [PREFERRED, WARN, REJECTED].findIndex((candidate) => text.includes(candidate.releaseName)));
    expect(ascending).toEqual([2, 1, 0]);
    fireEvent.click(screen.getByRole("button", { name: /^Decision/ }));
    await waitFor(() => expect(order()[0]).toContain(PREFERRED.releaseName));
  });

  it("clears the protocol filter whenever a fresh search is run", async () => {
    setup(RESPONSES([{ ...PREFERRED, seeders: 40 }, { ...ELIGIBLE, seeders: 90 }, { ...ELIGIBLE, releaseName: "Big.Buck.Bunny.2008.480p.DVD-X", indexerProtocol: "newznab" }]));
    await renderPage();
    press("Choose a release");
    await screen.findByText(PREFERRED.releaseName);
    fireEvent.click(screen.getByRole("combobox", { name: "Filter by protocol" }));
    fireEvent.click(await screen.findByRole("option", { name: "Usenet" }));
    expect(await screen.findByText("1 of 3 candidates")).toBeInTheDocument();

    press("Choose a release");
    await waitFor(() => expect(calls.filter((call) => call.url.startsWith("/api/movies/m1/search?mode=preview"))).toHaveLength(2));
    expect(await screen.findByText("3 of 3 candidates")).toBeInTheDocument();
    expect(order()[0]).toContain(PREFERRED.releaseName);
  });

  it("opens the collapsed Force with the engine's own summary as the reason it suggests", async () => {
    setup(RESPONSES([PREFERRED, ELIGIBLE]));
    await renderPage();
    press("Choose a release");
    fireEvent.click(await screen.findByText(ELIGIBLE.releaseName));
    const drawer = await screen.findByRole("dialog", { name: ELIGIBLE.releaseName });
    fireEvent.click(within(drawer).getAllByRole("button", { name: "Cancel" })[0]);
    expect(within(drawer).queryByRole("textbox", { name: "Why force this release?" })).toBeNull();

    fireEvent.click(within(drawer).getByRole("button", { name: "Force" }));
    expect(within(drawer).getByRole("textbox", { name: "Why force this release?" })).toHaveValue(ELIGIBLE.summary);
  });
});

describe("the Next step card after Choose a release (low rows)", () => {
  it("Review candidates brings a person on another tab back to the candidates", async () => {
    setup(RESPONSES([PREFERRED, ELIGIBLE]));
    await renderPage();
    press("Choose a release");
    const review = await screen.findByRole("button", { name: "Review candidates" });
    expect(screen.getByText(PREFERRED.releaseName)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("radio", { name: "History" }));
    await waitFor(() => expect(screen.queryByText(PREFERRED.releaseName)).toBeNull());

    fireEvent.click(screen.getByRole("button", { name: "Review candidates" }));
    expect(await screen.findByText(PREFERRED.releaseName)).toBeInTheDocument();
    expect(screen.getByText("2 of 2 candidates")).toBeInTheDocument();
    expect(review).toBeDefined();
  });
});

describe("the automatic search button while it runs (search-grab low rows)", () => {
  it("disables both search buttons and shows the spinner on Search now only", async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    setup();
    const inner = authedFetch.getMockImplementation()!;
    authedFetch.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).startsWith("/api/movies/m1/search")) await held;
      return inner(input, init);
    });
    await renderPage();
    const chooser = screen.getAllByRole("button", { name: "Choose a release" })[0];
    const searcher = screen.getAllByRole("button", { name: "Search now" })[0];

    fireEvent.click(searcher);

    await waitFor(() => expect(searcher).toBeDisabled());
    expect(chooser).toBeDisabled();
    expect(searcher.querySelector(".animate-spin")).not.toBeNull();
    expect(chooser.querySelector(".animate-spin")).toBeNull();
    release();
    await waitFor(() => expect(searcher).toBeEnabled());
  });
});
