import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The Calendar's entries (coverage register, area library): what the screen asks the server for, and where an entry takes you.
 */
function localToday(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

const mocks = vi.hoisted(() => ({ navigate: vi.fn(), fetchJson: vi.fn() }));

const loaderData = {
  episodes: [{
    episodeId: "e1", seriesId: "s1", seriesTitle: "Fringe Show", posterUrl: null, seasonNumber: 1, episodeNumber: 2, title: "Pilot",
    airDateUtc: new Date().toISOString(), monitored: true, wantedStatus: "missing"
  }],
  movies: [
    { movieId: "m1", title: "In Cinemas Movie", releaseYear: 2026, posterUrl: null, kind: "inCinemas" as const, date: localToday(), monitored: true, wantedStatus: "missing" },
    { movieId: "m2", title: "Quiet Movie", releaseYear: 2026, posterUrl: null, kind: "digital" as const, date: localToday(), monitored: false, wantedStatus: "missing" }
  ]
};

vi.mock("react-router-dom", async (importOriginal) => ({ ...(await importOriginal<typeof import("react-router-dom")>()), useNavigate: () => mocks.navigate }));
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return { ...(await importOriginal<typeof import("../lib/route-cache")>()), useRouteSources: () => readyRouteSources(loaderData, vi.fn()) };
});

const { CalendarPage, calendarLoader } = await import("./calendar-page");

beforeEach(() => {
  window.localStorage.clear();
  window.sessionStorage.clear();
  mocks.navigate.mockReset();
  mocks.fetchJson.mockReset();
});

describe("clicking an entry", () => {
  it("opens that film's page, and that show's page for an episode", () => {
    render(<MemoryRouter><CalendarPage /></MemoryRouter>);

    fireEvent.click(screen.getByRole("button", { name: /In Cinemas Movie/ }));
    expect(mocks.navigate).toHaveBeenLastCalledWith("/movies/m1");
    fireEvent.click(screen.getByRole("button", { name: /Fringe Show/ }));
    expect(mocks.navigate).toHaveBeenLastCalledWith("/tv/s1");
  });

  it("an unmonitored entry is still there to open, drawn apart from a monitored one", () => {
    render(<MemoryRouter><CalendarPage /></MemoryRouter>);

    const quiet = screen.getByRole("button", { name: /Quiet Movie/ });
    expect(quiet.className).toMatch(/border-dashed/);
    expect(screen.getByRole("button", { name: /In Cinemas Movie/ }).className).not.toMatch(/border-dashed/);
    fireEvent.click(quiet);
    expect(mocks.navigate).toHaveBeenLastCalledWith("/movies/m2");
  });
});

describe("what the screen asks the server for", () => {
  it("one wide window of episodes and one of films, in the forms each route reads", async () => {
    mocks.fetchJson.mockImplementation(async () => []);

    const loaded = calendarLoader();
    await Promise.resolve(loaded);
    await vi.waitFor(() => expect(mocks.fetchJson.mock.calls.length).toBeGreaterThanOrEqual(2));

    const urls = mocks.fetchJson.mock.calls.map(([url]) => String(url));
    const episodes = new URL(urls.find((url) => url.startsWith("/api/series/calendar?"))!, "http://x");
    const movies = new URL(urls.find((url) => url.startsWith("/api/movies/calendar?"))!, "http://x");
    const from = new Date(episodes.searchParams.get("from")!);
    const to = new Date(episodes.searchParams.get("to")!);
    expect(Math.round((Date.now() - from.getTime()) / 86_400_000)).toBe(45);
    expect(Math.round((to.getTime() - Date.now()) / 86_400_000)).toBe(120);
    expect(movies.searchParams.get("from")).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(movies.searchParams.get("to")).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });
});
