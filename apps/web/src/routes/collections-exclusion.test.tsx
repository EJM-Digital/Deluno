import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MovieCollectionItem, MovieCollectionMemberItem } from "../lib/api";

const authedFetch = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));
vi.mock("../components/app/tag-picker", () => ({ TagPicker: () => null }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const { CollectionMembers, CollectionsPage } = await import("./collections-page");

function member(overrides: Partial<MovieCollectionMemberItem>): MovieCollectionMemberItem {
  return {
    collectionId: "c1", providerId: "1", title: "Dune", releaseYear: 2021, overview: null, posterUrl: null,
    backdropUrl: null, externalUrl: null, imdbId: null, localMovieId: null, isExcluded: false,
    updatedUtc: "2026-10-03T00:00:00Z", ...overrides
  };
}

const collection = {
  id: "c1", provider: "tmdb", providerId: "726871", name: "Dune Collection", overview: null, posterUrl: null,
  backdropUrl: null, libraryId: "l1", libraryName: "Movies", rootPath: "/media/movies", monitored: true,
  monitorMovies: true, qualityProfileId: null, qualityProfileName: null, minimumAvailability: "released",
  searchOnAdd: false, memberCount: 2, heldCount: 0, missingCount: 2, lastSyncedUtc: null, nextSyncUtc: null,
  lastSyncError: null, createdUtc: "2026-10-03T00:00:00Z", updatedUtc: "2026-10-03T00:00:00Z", tags: ""
} as MovieCollectionItem;

describe("films in a followed collection (exclude and include again)", () => {
  it("offers Exclude on a film that is wanted, and says which film", () => {
    const onSetExcluded = vi.fn();
    const dune = member({});
    render(<CollectionMembers members={[dune]} busyProviderId={null} onSetExcluded={onSetExcluded} />);

    fireEvent.click(screen.getByRole("button", { name: "Exclude Dune (2021)" }));

    expect(onSetExcluded).toHaveBeenCalledWith(dune, true);
    expect(screen.queryByText("Excluded")).not.toBeInTheDocument();
  });

  it("shows an excluded film muted, with an Excluded chip and Include again", () => {
    const onSetExcluded = vi.fn();
    const part2 = member({ providerId: "2", title: "Dune: Part Two", releaseYear: 2024, isExcluded: true });
    const { container } = render(<CollectionMembers members={[part2]} busyProviderId={null} onSetExcluded={onSetExcluded} />);

    expect(screen.getByText("Excluded")).toBeInTheDocument();
    expect(container.querySelector("li")).toHaveAttribute("data-excluded", "true");
    expect(screen.queryByRole("button", { name: /^Exclude / })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Include again Dune: Part Two (2024)" }));
    expect(onSetExcluded).toHaveBeenCalledWith(part2, false);
  });

  it("disables only the row that is being saved", () => {
    render(<CollectionMembers members={[member({}), member({ providerId: "2", title: "Dune: Part Two" })]} busyProviderId="2" onSetExcluded={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Exclude Dune (2021)" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Exclude Dune: Part Two (2021)" })).toBeDisabled();
  });
});

describe("the Collections page", () => {
  beforeEach(() => {
    authedFetch.mockReset();
    authedFetch.mockResolvedValue({ ok: true, text: async () => "" });
  });

  function renderPage(members: MovieCollectionMemberItem[]) {
    const router = createMemoryRouter(
      [{
        path: "/",
        element: <CollectionsPage />,
        loader: () => ({ collections: [collection], libraries: [], qualityProfiles: [], membersByCollection: { c1: members } })
      }],
      { initialEntries: ["/"] }
    );
    return render(<RouterProvider router={router} />);
  }

  it("says how many films are excluded only when some are", async () => {
    renderPage([member({}), member({ providerId: "2", title: "Dune: Part Two", isExcluded: true })]);
    // The page loads on first use, which takes long on a busy machine: wait for the count itself, not for a set time.
    expect(await screen.findByText("1 excluded", {}, { timeout: 30_000 })).toBeInTheDocument();
  }, 40_000);

  it("shows no excluded count when none are excluded", async () => {
    renderPage([member({})]);
    await screen.findByRole("heading", { name: "Dune Collection" });
    expect(screen.queryByText(/ excluded$/)).not.toBeInTheDocument();
  });

  it("sends the exclusion for the film that was clicked", async () => {
    renderPage([member({ providerId: "42" })]);
    fireEvent.click(await screen.findByRole("button", { name: "Exclude Dune (2021)" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    const [url, init] = authedFetch.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/movie-collections/c1/members/42/exclusion");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(String(init.body))).toEqual({ excluded: true });
  });
});
