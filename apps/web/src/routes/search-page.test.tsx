import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../lib/use-auth";

/**
 * #753: the dedicated multi-indexer manual Search page -- a free-text query
 * fanned out to every enabled indexer, with a combined, sortable results
 * table and a grab flow that sends a hand-picked release straight to a
 * chosen download client.
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>())
}));

const indexers = [
  { id: "idx-1", name: "EZTV", protocol: "torznab", privacy: "public", baseUrl: "https://fixture.invalid", priority: 1, categories: "2000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "movies" }
];

const downloadClients = [{ id: "client-1", name: "qBittorrent", protocol: "qbittorrent" }];

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
      return new Response(JSON.stringify({ succeeded: true, status: "sent", message: "Sent" }), { status: 200 });
    }
    if (url.includes("/api/search")) {
      return new Response(JSON.stringify(searchResult), { status: 200 });
    }
    if (url.includes("/api/indexers")) {
      return new Response(JSON.stringify(indexers), { status: 200 });
    }
    if (url.includes("/api/download-clients")) {
      return new Response(JSON.stringify(downloadClients), { status: 200 });
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

  it("searches and shows enriched result columns", async () => {
    mockFetch();
    renderPage();

    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "Example" } });
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));

    await waitFor(() => expect(screen.getByText("Example.Release.WEB.1080p")).toBeTruthy());
    expect(screen.getByText("Torrent")).toBeTruthy();
    expect(screen.getByText("12 / 3")).toBeTruthy();

    const searchCall = vi.mocked(authedFetch).mock.calls.find(([url]) => (url as string).includes("/api/search") && !(url as string).includes("grab"));
    expect(searchCall).toBeTruthy();
    expect(JSON.parse((searchCall![1] as RequestInit).body as string)).toMatchObject({ query: "Example", searchType: "movie" });
  });

  it("grabs a result by picking a download client", async () => {
    mockFetch();
    renderPage();

    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "Example" } });
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));
    await waitFor(() => expect(screen.getByText("Example.Release.WEB.1080p")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Grab" }));
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Download client" })).toBeTruthy());
    fireEvent.change(screen.getByRole("combobox", { name: "Download client" }), { target: { value: "client-1" } });
    fireEvent.click(screen.getByRole("button", { name: "Send to download client" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/search/grab",
      expect.objectContaining({ method: "POST" })
    ));
    const grabCall = vi.mocked(authedFetch).mock.calls.find(([url]) => (url as string).includes("/api/search/grab"));
    expect(JSON.parse((grabCall![1] as RequestInit).body as string)).toMatchObject({ downloadClientId: "client-1" });
  });
});
