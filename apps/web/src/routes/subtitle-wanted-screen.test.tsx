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
  languagesMissing: ["en"]
};
let loaderData: { wanted: (typeof wantedRow)[] } = { wanted: [wantedRow] };

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
  });

  it("shows the empty state once nothing is missing", () => {
    loaderData = { wanted: [] };
    renderPage();
    expect(screen.getByText("Nothing is missing a subtitle")).toBeInTheDocument();
    loaderData = { wanted: [wantedRow] };
  });

  it("search all posts every listed item to the bulk endpoint", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ queued: 1 }) });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Search all" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/subtitles/wanted/search-all");
    expect(JSON.parse(init.body as string)).toEqual({
      items: [{ kind: "Movie", mediaId: "movie-1", libraryId: "library-1" }]
    });
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
