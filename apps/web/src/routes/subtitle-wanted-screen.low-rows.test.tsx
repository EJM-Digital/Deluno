import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("sonner", async (importOriginal) => ({
  ...(await importOriginal<typeof import("sonner")>()),
  toast: Object.assign(vi.fn(), { success: (...a: unknown[]) => toastSuccess(...a), error: (...a: unknown[]) => toastError(...a) })
}));

const revalidate = vi.fn();
const row = (overrides: Record<string, unknown>) => ({
  kind: "Movie", mediaId: "movie-1", libraryId: "library-1", libraryName: "Movies", title: "Dune", year: 2021,
  seasonNumber: null, episodeNumber: null, episodeTitle: null, languagesMissing: ["en"], lastAttempt: null, ...overrides
});
let loaderData: { wanted: ReturnType<typeof row>[] } = { wanted: [] };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SubtitleWantedPage } = await import("./subtitle-wanted-screen");

function renderPage() {
  return render(<MemoryRouter><SubtitleWantedPage /></MemoryRouter>);
}

afterEach(() => {
  revalidate.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  vi.unstubAllGlobals();
});

describe("Subtitle Wanted (low rows)", () => {
  it("a series row reads S01E05 with the episode's title, and a movie row shows its year", () => {
    loaderData = {
      wanted: [
        row({ kind: "Series", mediaId: "ep-1", title: "Fringe Show", seasonNumber: 1, episodeNumber: 5, episodeTitle: "The Arrival", year: null }),
        row({ title: "Dune", year: 2021 })
      ]
    };
    renderPage();

    expect(screen.getByText("S01E05 — The Arrival")).toBeInTheDocument();
    expect(screen.getByText("2021")).toBeInTheDocument();
  });

  it.each([
    [1, "Queued 1 search."],
    [3, "Queued 3 searches."]
  ])("Search all with %i queued says '%s'", async (queued, words) => {
    loaderData = { wanted: [row({})] };
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ queued }), { status: 200, headers: { "Content-Type": "application/json" } })));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Search all" }));

    await waitFor(() => expect(toastSuccess.mock.calls.length + toastError.mock.calls.length).toBeGreaterThan(0));
    expect(toastError.mock.calls).toEqual([]);
    expect(toastSuccess).toHaveBeenCalledWith(words);
    expect(revalidate).toHaveBeenCalled();
  });

  it("while a search runs the buttons are disabled", async () => {
    loaderData = { wanted: [row({})] };
    vi.stubGlobal("fetch", vi.fn(() => new Promise(() => undefined)));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Search all" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Search subtitles for Dune" })).toBeDisabled());
  });

  it("a refused search shows the plain failure toast with its explanation", async () => {
    loaderData = { wanted: [row({})] };
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ message: "The subtitle search could not start." }), { status: 500, headers: { "Content-Type": "application/json" } })));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Search all" }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    const [title, options] = toastError.mock.calls[0] as [string, { description?: string }];
    expect(title.length).toBeGreaterThan(0);
    expect(options).toHaveProperty("description");
    expect(toastSuccess).not.toHaveBeenCalled();
  });
});
