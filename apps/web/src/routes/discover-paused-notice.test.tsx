import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { LibraryItem, QualityProfileItem } from "../lib/api";

/** Discover's add drawer draws the paused notice only while "Search for it as soon as it is added" is on (#936). */

const authedFetch = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({
    libraries: [{ id: "lib-1", name: "Movies", mediaType: "movies" } as LibraryItem],
    qualityProfiles: [{ id: "profile-1", name: "HD-1080p", mediaType: "movies" } as QualityProfileItem]
  })
}));

const json = (body: unknown) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)) } as Response);
const matrix = { provider: "tmdb", providerId: "603", title: "The Matrix", year: 1999, overview: "", posterUrl: null, backdropUrl: null, rating: 8.2, imdbId: "tt0133093", externalUrl: null, inLibrary: false, libraryEntryId: null, excluded: false };

describe("Discover while automation is paused", () => {
  it("says the film will not be searched while the search-on-add switch is on, and says nothing once it is switched off", async () => {
    authedFetch.mockImplementation((path: string) => {
      if (path === "/api/settings") return json({ autoStartJobs: false });
      if (path.startsWith("/api/discover")) return json({ popular: [matrix], lists: [] });
      return json([]);
    });
    const { DiscoverPage } = await import("./discover-page");
    render(<MemoryRouter><DiscoverPage /></MemoryRouter>);
    await waitFor(() => expect(screen.getByText("The Matrix")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Add The Matrix" }));
    await screen.findByRole("dialog", { name: /Add The Matrix/ });
    expect(await screen.findByText("Automation is paused, so this film won't be searched until you resume it.")).toBeVisible();

    fireEvent.click(screen.getByRole("switch", { name: /Search for it as soon as it is added/ }));
    await waitFor(() => expect(screen.queryByText(/Automation is paused/)).not.toBeInTheDocument());
  });
});
