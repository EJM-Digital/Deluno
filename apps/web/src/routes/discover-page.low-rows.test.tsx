import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DiscoverResponse, LibraryItem, QualityProfileItem } from "../lib/api";

const authedFetch = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));

const loaderData = { libraries: [] as LibraryItem[], qualityProfiles: [] as QualityProfileItem[] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData
}));

const popular: DiscoverResponse = {
  popular: [{ provider: "tmdb", providerId: "603", title: "The Matrix", year: 1999, overview: "A hacker.", posterUrl: null, backdropUrl: null, rating: 8.2, imdbId: null, externalUrl: null, inLibrary: false, libraryEntryId: null, excluded: false }],
  lists: []
};
const json = (body: unknown, status = 200) => Promise.resolve({ ok: status < 400, status, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)) } as Response);

const { DiscoverPage } = await import("./discover-page");
const page = () => render(<MemoryRouter><DiscoverPage /></MemoryRouter>);

beforeEach(() => {
  window.localStorage.clear();
  authedFetch.mockReset();
  loaderData.libraries = [];
  loaderData.qualityProfiles = [];
});

describe("Discover (low rows)", () => {
  it("shows a loading skeleton until the list arrives", async () => {
    authedFetch.mockImplementation(() => new Promise(() => undefined));
    const { container } = page();

    await waitFor(() => expect(container.querySelector("[aria-busy='true']")).not.toBeNull());
    expect(screen.queryByText("The Matrix")).toBeNull();
  });

  it("says 'Discover could not be loaded' with the reason when the list cannot be read", async () => {
    authedFetch.mockImplementation(() => json({ message: "The metadata service did not answer." }, 404));
    page();

    expect(await screen.findByText("Discover could not be loaded")).toBeVisible();
    expect(screen.queryByText(/provider configured/i)).toBeNull();
  });

  it("with no library set up the add drawer says so and Add is disabled", async () => {
    authedFetch.mockImplementation((path: string) => (path.startsWith("/api/discover") ? json(popular) : json([])));
    page();
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Add The Matrix" }));

    expect(await screen.findByText(/No movie library is set up yet/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Add" })).toBeDisabled();
    expect(authedFetch.mock.calls.some(([path]) => path === "/api/discover/add")).toBe(false);
  });
});
