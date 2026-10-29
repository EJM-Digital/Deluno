import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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
    // One clear choice, ticked by default (#1145); the profile follows the library unless another is picked.
    expect(body.searchForMissingNow).toBe(true);
    expect(body.searchForCutoffUnmetNow).toBe(true);
    expect(body.qualityProfileId).toBeNull();
  });

  it("sends nothing to search when the search-on-add switch is turned off (#1145)", async () => {
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

    fireEvent.click(screen.getByRole("switch", { name: /Search for it as soon as it is added/ }));
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith("/api/discover/add", expect.anything()));
    const addCall = authedFetch.mock.calls.filter(([path]) => path === "/api/discover/add").at(-1)!;
    const [, init] = addCall as [string, RequestInit];
    const body = JSON.parse(init.body as string);
    expect(body.searchForMissingNow).toBe(false);
    expect(body.searchForCutoffUnmetNow).toBe(false);
  });
});

describe("Discover list entries carry their own status", () => {
  it("every entry says Would add, Already in library, Excluded or Not eligible, and only its own status", async () => {
    const entry = (title: string, status: string, inLibrary: boolean, excluded: boolean) => ({ title, year: 2020, imdbId: null, status, reason: "Why.", inLibrary, excluded });
    await renderDiscoverPage(discoverResponse({
      lists: [{ sourceId: "source-1", sourceName: "Trakt Popular", items: [
        entry("Fresh Pick", "would add", false, false),
        entry("Held Pick", "already in library", true, false),
        entry("Dropped Pick", "excluded", false, true),
        entry("Ineligible Pick", "not eligible", false, false)
      ] }]
    }));

    await waitFor(() => expect(screen.getByText("Trakt Popular")).toBeInTheDocument());
    const row = (title: string) => screen.getByText(title).closest("li") ?? screen.getByText(title).parentElement!.parentElement!;
    expect(within(row("Fresh Pick")).getByText("Would add")).toBeInTheDocument();
    expect(within(row("Held Pick")).getByText("Already in library")).toBeInTheDocument();
    expect(within(row("Dropped Pick")).getAllByText("Excluded")).toHaveLength(1);
    const manage = within(row("Dropped Pick")).getByRole("link", { name: "Manage exclusions for Dropped Pick" });
    expect(manage).toHaveAttribute("href", "/settings/lists");
    expect(within(row("Dropped Pick")).queryByText("Would add")).toBeNull();
    expect(within(row("Ineligible Pick")).getByText("Not eligible")).toBeInTheDocument();
    expect(within(row("Held Pick")).getByText("Held")).toBeInTheDocument();
    expect(within(row("Dropped Pick")).queryByRole("button", { name: "Add Dropped Pick" })).toBeNull();
    expect(within(row("Fresh Pick")).getByRole("button", { name: "Add Fresh Pick" })).toBeInTheDocument();
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

describe("Discover Movies and TV chips", () => {
  it("the Movies and TV chips ask for that list, and the choice is remembered in this browser for the next visit", async () => {
    window.localStorage.clear();
    await renderDiscoverPage();
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());
    expect(authedFetch).toHaveBeenLastCalledWith("/api/discover?mediaType=movies", expect.anything());

    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    await waitFor(() => expect(authedFetch).toHaveBeenLastCalledWith("/api/discover?mediaType=series", expect.anything()));
    expect(window.localStorage.getItem("deluno-discover-type")).toBe("series");

    cleanup();
    await renderDiscoverPage();
    await waitFor(() => expect(authedFetch).toHaveBeenLastCalledWith("/api/discover?mediaType=series", expect.anything()));
    expect(screen.getByRole("radio", { name: "TV" })).toHaveAttribute("aria-checked", "true");
  });
});

describe("Discover lists and filters (#1429)", () => {
  function lastDiscoverPath() {
    return authedFetch.mock.calls.map(([path]) => path as string).filter((path) => path.startsWith("/api/discover?")).at(-1)!;
  }

  it("opens on the popular list and switches to trending, top rated and the films-only lists by name", async () => {
    window.localStorage.clear();
    await renderDiscoverPage();
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());
    expect(screen.getByRole("heading", { name: "Popular on TMDb" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("radio", { name: "Trending" }));
    await waitFor(() => expect(lastDiscoverPath()).toBe("/api/discover?mediaType=movies&list=trending"));
    expect(screen.getByRole("heading", { name: "Trending this week on TMDb" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("radio", { name: "Upcoming" }));
    await waitFor(() => expect(lastDiscoverPath()).toBe("/api/discover?mediaType=movies&list=upcoming"));
    expect(screen.getByRole("radio", { name: "In cinemas" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    await waitFor(() => expect(lastDiscoverPath()).toBe("/api/discover?mediaType=series"));
    expect(screen.queryByRole("radio", { name: "Upcoming" })).toBeNull();
    expect(screen.getByRole("radio", { name: "On air" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Popular" })).toHaveAttribute("aria-checked", "true");
  });

  it("filters by release, years, genre, rating, language and order, says so, and clears them again", async () => {
    window.localStorage.clear();
    await renderDiscoverPage();
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());
    expect(screen.queryByRole("group", { name: "Discover filters" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Filters" }));
    const bar = screen.getByRole("group", { name: "Discover filters" });
    fireEvent.change(within(bar).getByRole("combobox", { name: "Release" }), { target: { value: "upcoming" } });
    await waitFor(() => expect(lastDiscoverPath()).toBe("/api/discover?mediaType=movies&release=upcoming"));
    expect(screen.getByRole("heading", { name: "Titles that match your filters" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Filters, 1 on" })).toBeInTheDocument();
    expect(screen.getByText(/Showing films: not out yet\./)).toBeInTheDocument();
    for (const chip of within(screen.getByRole("radiogroup", { name: "Discover list" })).getAllByRole("radio")) expect(chip).toHaveAttribute("aria-checked", "false");

    fireEvent.change(within(bar).getByRole("textbox", { name: "From year" }), { target: { value: "20a27" } });
    fireEvent.change(within(bar).getByRole("textbox", { name: "To year" }), { target: { value: "2030" } });
    fireEvent.click(within(bar).getByRole("button", { name: "Action" }));
    fireEvent.click(within(bar).getByRole("button", { name: "Comedy" }));
    expect(within(bar).getByRole("button", { name: "Action" })).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(within(bar).getByRole("button", { name: "Comedy" }));
    expect(within(bar).getByRole("button", { name: "Comedy" })).toHaveAttribute("aria-pressed", "false");
    fireEvent.change(within(bar).getByRole("combobox", { name: "Lowest rating" }), { target: { value: "7" } });
    fireEvent.change(within(bar).getByRole("combobox", { name: "Original language" }), { target: { value: "ja" } });
    fireEvent.change(within(bar).getByRole("combobox", { name: "Order" }), { target: { value: "rating" } });
    await waitFor(() => expect(lastDiscoverPath()).toBe(
      "/api/discover?mediaType=movies&release=upcoming&yearFrom=2027&yearTo=2030&genres=28&ratingMin=7&language=ja&sort=rating"));
    expect(screen.getByRole("button", { name: "Filters, 6 on" })).toBeInTheDocument();
    expect(screen.getByText(/Showing films: not out yet, 2027 to 2030, Action, rated 7\+, in Japanese\./)).toBeInTheDocument();

    fireEvent.click(within(bar).getByRole("button", { name: "Clear filters" }));
    await waitFor(() => expect(lastDiscoverPath()).toBe("/api/discover?mediaType=movies"));
    expect(screen.getByRole("button", { name: "Filters" })).toBeInTheDocument();
    expect(screen.queryByText(/^Showing films/)).toBeNull();
  });

  it("choosing a list drops the filters, and choosing Movies or TV drops them too", async () => {
    window.localStorage.clear();
    await renderDiscoverPage();
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Filters" }));
    fireEvent.change(screen.getByRole("combobox", { name: "Lowest rating" }), { target: { value: "8" } });
    await waitFor(() => expect(lastDiscoverPath()).toContain("ratingMin=8"));

    fireEvent.click(screen.getByRole("radio", { name: "Top rated" }));
    await waitFor(() => expect(lastDiscoverPath()).toBe("/api/discover?mediaType=movies&list=top-rated"));
    expect(screen.getByRole("button", { name: "Filters" })).toBeInTheDocument();

    fireEvent.change(screen.getByRole("combobox", { name: "Lowest rating" }), { target: { value: "6" } });
    await waitFor(() => expect(lastDiscoverPath()).toContain("ratingMin=6"));
    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    await waitFor(() => expect(lastDiscoverPath()).toBe("/api/discover?mediaType=series"));
    expect(screen.getByRole("radio", { name: "Popular" })).toHaveAttribute("aria-checked", "true");
    expect(within(screen.getByRole("group", { name: "Genres of shows" })).getByRole("button", { name: "Kids" })).toBeInTheDocument();
  });

  it("offers more pages until TMDb has no more, and says when filters leave nothing", async () => {
    window.localStorage.clear();
    authedFetch.mockImplementation((path: string) => {
      if (path.startsWith("/api/discover?")) {
        if (path.includes("ratingMin=8")) return jsonResponse(discoverResponse({ popular: [], lists: [] }));
        if (path.includes("page=2")) {
          return jsonResponse(discoverResponse({ popular: [
            { ...discoverResponse().popular[0] },
            { ...discoverResponse().popular[0], providerId: "999", title: "Page Two Film" }
          ], lists: [] }));
        }
        return jsonResponse(discoverResponse());
      }
      return jsonResponse([]);
    });
    const { DiscoverPage } = await import("./discover-page");
    render(<MemoryRouter><DiscoverPage /></MemoryRouter>);
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Show more" }));
    await waitFor(() => expect(screen.getByText("Page Two Film")).toBeInTheDocument());
    expect(screen.getAllByText("The Matrix")).toHaveLength(1);
    expect(authedFetch).toHaveBeenCalledWith("/api/discover?mediaType=movies&page=2", expect.anything());

    fireEvent.click(screen.getByRole("button", { name: "Show more" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "That is everything TMDb lists" })).toBeDisabled());

    fireEvent.click(screen.getByRole("button", { name: "Filters" }));
    fireEvent.change(screen.getByRole("combobox", { name: "Lowest rating" }), { target: { value: "8" } });
    await waitFor(() => expect(screen.getByText("Nothing matches those filters")).toBeInTheDocument());
    expect(screen.getByText(/Loosen a filter/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Show more|everything TMDb lists/ })).toBeNull();
  });
});
