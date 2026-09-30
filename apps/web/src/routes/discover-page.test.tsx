import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { DiscoverResponse, LibraryItem, QualityProfileItem } from "../lib/api";

/**
 * #785: Discover shows TMDb's own popular list plus every configured list's
 * entries, marks what is already held or excluded, and adds a title with the
 * same root/quality-profile/monitor choices the existing add-title flow uses.
 */

const authedFetch = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));

const libraries: LibraryItem[] = [
  { id: "lib-1", name: "Movies", mediaType: "movies" } as LibraryItem
];
const qualityProfiles: QualityProfileItem[] = [
  { id: "profile-1", name: "HD-1080p", mediaType: "movies" } as QualityProfileItem
];

const loaderData = { libraries, qualityProfiles };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData
}));

function discoverResponse(overrides: Partial<DiscoverResponse> = {}): DiscoverResponse {
  return {
    popular: [
      {
        provider: "tmdb", providerId: "603", title: "The Matrix", year: 1999,
        overview: "A hacker learns the truth.", posterUrl: "https://image.tmdb.org/matrix.jpg",
        backdropUrl: null, rating: 8.2, imdbId: "tt0133093", externalUrl: null,
        inLibrary: false, libraryEntryId: null, excluded: false
      },
      {
        provider: "tmdb", providerId: "27205", title: "Inception", year: 2010,
        overview: "A thief who steals secrets.", posterUrl: null, backdropUrl: null,
        rating: 8.4, imdbId: "tt1375666", externalUrl: null,
        inLibrary: true, libraryEntryId: "movie-1", excluded: false
      }
    ],
    lists: [
      {
        sourceId: "source-1",
        sourceName: "Trakt Popular",
        items: [
          { title: "Would Add Movie", year: 2020, imdbId: null, status: "would add", reason: "Passes filters.", inLibrary: false, excluded: false },
          { title: "Already Held Movie", year: 2019, imdbId: null, status: "already in library", reason: "Have it.", inLibrary: true, excluded: false }
        ]
      }
    ],
    ...overrides
  };
}

function jsonResponse(body: unknown) {
  return Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body))
  } as Response);
}

/** `fetchJson` (the GET the page's effect uses) goes through `authedFetch` too, same as every write call. */
function mockFetchJsonForDiscover(response: DiscoverResponse) {
  authedFetch.mockImplementation((path: string) => {
    if (path.startsWith("/api/discover")) return jsonResponse(response);
    return jsonResponse([]);
  });
}

async function renderDiscoverPage(response: DiscoverResponse = discoverResponse()) {
  mockFetchJsonForDiscover(response);
  const { DiscoverPage } = await import("./discover-page");
  render(
    <MemoryRouter>
      <DiscoverPage />
    </MemoryRouter>
  );
}

describe("Discover page (#785)", () => {
  it("shows TMDb's popular titles, marking one already held", async () => {
    await renderDiscoverPage();

    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());
    expect(screen.getByText("Inception")).toBeInTheDocument();
    expect(screen.getByText("In library")).toBeInTheDocument();
  });

  it("shows entries from a configured list, each with its own status", async () => {
    await renderDiscoverPage();

    await waitFor(() => expect(screen.getByText("Trakt Popular")).toBeInTheDocument());
    expect(screen.getByText("Would Add Movie")).toBeInTheDocument();
    expect(screen.getByText("Already Held Movie")).toBeInTheDocument();
    expect(screen.getByText("Already in library")).toBeInTheDocument();
  });

  it("adds a popular title with the chosen library, profile and monitor choice", async () => {
    const response = discoverResponse();
    authedFetch.mockImplementation((path: string) => {
      if (path === "/api/discover/add") {
        return jsonResponse({ id: "movie-99", mediaType: "movies", libraryId: "lib-1" });
      }
      if (path.startsWith("/api/discover")) return jsonResponse(response);
      return jsonResponse([]);
    });

    const { DiscoverPage } = await import("./discover-page");
    render(
      <MemoryRouter>
        <DiscoverPage />
      </MemoryRouter>
    );
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Add The Matrix" }));

    await waitFor(() => expect(screen.getByRole("dialog", { name: /Add The Matrix/ })).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith("/api/discover/add", expect.anything()));
    const addCall = authedFetch.mock.calls.find(([path]) => path === "/api/discover/add")!;
    const [, init] = addCall as [string, RequestInit];
    const body = JSON.parse(init.body as string);
    expect(body.title).toBe("The Matrix");
    expect(body.libraryId).toBe("lib-1");
    expect(body.monitored).toBe(true);
    expect(body.searchForMissingNow).toBe(false);
    expect(body.searchForCutoffUnmetNow).toBe(false);
  });

  it("sends search-on-add choices when their switches are turned on, off by default", async () => {
    const response = discoverResponse();
    authedFetch.mockImplementation((path: string) => {
      if (path === "/api/discover/add") {
        return jsonResponse({ id: "movie-99", mediaType: "movies", libraryId: "lib-1" });
      }
      if (path.startsWith("/api/discover")) return jsonResponse(response);
      return jsonResponse([]);
    });

    const { DiscoverPage } = await import("./discover-page");
    render(
      <MemoryRouter>
        <DiscoverPage />
      </MemoryRouter>
    );
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Add The Matrix" }));
    await waitFor(() => expect(screen.getByRole("dialog", { name: /Add The Matrix/ })).toBeInTheDocument());

    fireEvent.click(screen.getByRole("switch", { name: /Search for missing now/ }));
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith("/api/discover/add", expect.anything()));
    const addCall = authedFetch.mock.calls.filter(([path]) => path === "/api/discover/add").at(-1)!;
    const [, init] = addCall as [string, RequestInit];
    const body = JSON.parse(init.body as string);
    expect(body.searchForMissingNow).toBe(true);
    expect(body.searchForCutoffUnmetNow).toBe(false);
  });
});

describe("Discover empty state (#1092)", () => {
  it("says the list could not be loaded, never that a provider needs configuring", async () => {
    await renderDiscoverPage(discoverResponse({ popular: [], lists: [] }));

    await waitFor(() => expect(screen.getByText("Nothing to show right now")).toBeInTheDocument());
    expect(screen.getByText(/could not be loaded just now/)).toBeInTheDocument();
    expect(screen.queryByText(/provider configured/i)).not.toBeInTheDocument();
  });
});
