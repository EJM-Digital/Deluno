import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../lib/use-auth";

/**
 * #753: the dedicated multi-indexer manual Search page -- a free-text query
 * fanned out to every enabled indexer, with a combined, sortable results
 * table and a grab flow that routes the release straight through the same
 * resolver a per-title grab uses (no download-client picker here).
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>())
}));

const indexers = [
  { id: "idx-1", name: "EZTV", protocol: "torznab", privacy: "public", baseUrl: "https://fixture.invalid", priority: 1, categories: "2000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "movies" }
];

const categories = [
  { value: 2000, label: "Movies" },
  { value: 2040, label: "Movies/HD" },
  { value: 5000, label: "TV" },
  { value: 5040, label: "TV/HD" }
];

const searchResult = {
  candidates: [
    {
      releaseName: "Example.Release.WEB.1080p",
      indexerId: "idx-1",
      indexerName: "EZTV",
      quality: "WEB 1080p",
      meetsCutoff: true,
      decisionStatus: "eligible",
      sizeBytes: 2_147_483_648,
      seeders: 12,
      leechers: 3,
      indexerProtocol: "torznab",
      releaseAgeHours: 5,
      downloadUrl: "https://fixture.invalid/x.torrent"
    }
  ],
  indexersQueried: 1
};

function mockFetch() {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/api/search/grab")) {
      return new Response(JSON.stringify({ succeeded: true, status: "sent", message: "Sent", downloadClientName: "qBittorrent" }), { status: 200 });
    }
    if (url.includes("/api/search/categories")) {
      return new Response(JSON.stringify(categories), { status: 200 });
    }
    if (url.includes("/api/search")) {
      return new Response(JSON.stringify(searchResult), { status: 200 });
    }
    if (url.includes("/api/indexers")) {
      return new Response(JSON.stringify(indexers), { status: 200 });
    }
    return new Response(JSON.stringify({}), { status: 200 });
  });
}

const { SearchPage } = await import("./search-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SearchPage />
    </MemoryRouter>
  );
}

describe("multi-indexer manual Search page (#753)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("refuses to search with an empty query", async () => {
    mockFetch();
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));

    await waitFor(() => expect(authedFetch).not.toHaveBeenCalledWith(
      expect.stringContaining("/api/search"),
      expect.objectContaining({ method: "POST" })
    ));
  });

  it("offers a basic search type alongside movie and TV", async () => {
    mockFetch();
    renderPage();

    expect(screen.getByRole("radio", { name: "Movie" })).toBeTruthy();
    expect(screen.getByRole("radio", { name: "TV" })).toBeTruthy();
    expect(screen.getByRole("radio", { name: "Basic" })).toBeTruthy();
  });

  it("searches and shows enriched result columns", async () => {
    mockFetch();
    renderPage();

    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "Example" } });
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));

    await waitFor(() => expect(screen.getByText("Example.Release.WEB.1080p")).toBeTruthy());
    expect(screen.getByText("Torrent")).toBeTruthy();
    expect(screen.getByText("12 / 3")).toBeTruthy();

    const searchCall = vi.mocked(authedFetch).mock.calls.find(([url]) => (url as string) === "/api/search");
    expect(searchCall).toBeTruthy();
    expect(JSON.parse((searchCall![1] as RequestInit).body as string)).toMatchObject({ query: "Example", searchType: "movie" });
  });

  it("lets categories relevant to the chosen type be picked and sends them with the search", async () => {
    mockFetch();
    renderPage();

    await waitFor(() => expect(screen.getByText("Movies/HD")).toBeTruthy());
    // TV categories are not relevant to a movie search.
    expect(screen.queryByText("TV/HD")).toBeNull();

    fireEvent.click(screen.getByRole("checkbox", { name: "Movies/HD" }));
    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "Example" } });
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));

    await waitFor(() => expect(screen.getByText("Example.Release.WEB.1080p")).toBeTruthy());
    const searchCall = vi.mocked(authedFetch).mock.calls.find(([url]) => (url as string) === "/api/search");
    expect(JSON.parse((searchCall![1] as RequestInit).body as string)).toMatchObject({ categoryIds: [2040] });
  });

  it("grabs a result without asking which download client to use", async () => {
    mockFetch();
    renderPage();

    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "Example" } });
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));
    await waitFor(() => expect(screen.getByText("Example.Release.WEB.1080p")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Grab" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Send to download client" })).toBeTruthy());
    expect(screen.queryByRole("combobox", { name: "Download client" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Send to download client" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/search/grab",
      expect.objectContaining({ method: "POST" })
    ));
    const grabCall = vi.mocked(authedFetch).mock.calls.find(([url]) => (url as string).includes("/api/search/grab"));
    const body = JSON.parse((grabCall![1] as RequestInit).body as string);
    expect(body.downloadClientId).toBeUndefined();
    expect(body).toMatchObject({ releaseName: "Example.Release.WEB.1080p", searchType: "movie" });
  });

  // #955: a search result never grabbed still names a release, so it gets
  // the same "Block this release" action the Queue, history and a title's
  // page already offer (cleanup sheet J5, #925).
  it("offers Block this release on a search result and blocks it", async () => {
    mockFetch();
    renderPage();

    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "Example" } });
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));
    await waitFor(() => expect(screen.getByText("Example.Release.WEB.1080p")).toBeTruthy());

    const blockButton = await screen.findByRole("button", { name: 'Block "Example.Release.WEB.1080p"' });
    fireEvent.click(blockButton);

    const confirmButton = await screen.findByRole("button", { name: "Block this release" });
    fireEvent.click(confirmButton);

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/blocked-releases",
      expect.objectContaining({ method: "POST" })
    ));
    const blockCall = vi.mocked(authedFetch).mock.calls.find(
      ([url, init]) => (url as string) === "/api/blocked-releases" && (init as RequestInit | undefined)?.method === "POST"
    );
    const body = JSON.parse((blockCall![1] as RequestInit).body as string);
    expect(body).toMatchObject({ releaseName: "Example.Release.WEB.1080p", indexerName: "EZTV", mediaType: "movie" });
  });
});
