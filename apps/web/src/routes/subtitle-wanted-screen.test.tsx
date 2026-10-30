import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * #840: the cross-library Wanted screen lists titles missing a wanted
 * subtitle language and can queue a search — either for everything shown,
 * or for one row at a time.
 */

const revalidate = vi.fn();
const wantedRow = {
  kind: "Movie",
  mediaId: "movie-1",
  libraryId: "library-1",
  libraryName: "Movies",
  title: "Dune",
  year: 2021,
  seasonNumber: null,
  episodeNumber: null,
  episodeTitle: null,
  languagesMissing: ["en"],
  lastAttempt: null as {
    language: string;
    provider: string | null;
    outcome: string;
    attemptedUtc: string;
    score: number | null;
  } | null
};
let loaderData: { wanted: (typeof wantedRow)[]; nextPageToken?: string | null; totalCount?: number } = { wanted: [wantedRow] };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SubtitleWantedPage } = await import("./subtitle-wanted-screen");

function renderPage() {
  return render(
    <MemoryRouter>
      <SubtitleWantedPage />
    </MemoryRouter>
  );
}

describe("Subtitle Wanted screen (#840)", () => {
  afterEach(() => {
    revalidate.mockReset();
    vi.unstubAllGlobals();
  });

  it("lists a title missing a language", () => {
    renderPage();
    expect(screen.getByText("Dune")).toBeInTheDocument();
    expect(screen.getByText("EN")).toBeInTheDocument();
    expect(screen.getByText("Not tried yet")).toBeInTheDocument();
  });

  it("shows the latest attempt on the row (#982)", () => {
    loaderData = {
      wanted: [
        {
          ...wantedRow,
          lastAttempt: {
            language: "en",
            provider: "gestdown",
            outcome: "Rejected: not found",
            attemptedUtc: "2026-09-28T00:00:00Z",
            score: null
          }
        }
      ]
    };
    renderPage();
    expect(screen.getByText(/Rejected: not found via gestdown/)).toBeInTheDocument();
    loaderData = { wanted: [wantedRow] };
  });

  it("says how many are waiting in all and loads the next page on request (#1439)", async () => {
    const second = { ...wantedRow, mediaId: "movie-2", title: "Arrival" };
    const fetchMock = vi.fn().mockImplementation(async () => new Response(
      JSON.stringify({ items: [second], nextPageToken: null, hasMore: false, totalCount: 2 }),
      { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    loaderData = { wanted: [wantedRow], nextPageToken: "tok", totalCount: 2 } as typeof loaderData;
    renderPage();
    expect(screen.getByText("2 short of a language")).toBeInTheDocument();
    expect(screen.getByText("Showing 1 of 2")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Load more" }));

    expect(await screen.findByText("Arrival")).toBeInTheDocument();
    expect(String(fetchMock.mock.calls[0][0])).toContain("pageToken=tok");
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
    loaderData = { wanted: [wantedRow] };
  });

  it("shows the empty state once nothing is missing", () => {
    loaderData = { wanted: [] };
    renderPage();
    expect(screen.getByText("Nothing is missing a subtitle")).toBeInTheDocument();
    loaderData = { wanted: [wantedRow] };
  });

  it("search all asks the server to search the whole list, not only the loaded pages", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ queued: 1 }) });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Search all" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/subtitles/wanted/search-all");
    // The whole list is searched on the server, not only the pages loaded here (#1439).
    expect(JSON.parse(init.body as string)).toEqual({ all: true });
  });

  it("a single row's own search button posts just that row", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ queued: 1 }) });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Search subtitles for Dune" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string).items).toHaveLength(1);
  });
});
