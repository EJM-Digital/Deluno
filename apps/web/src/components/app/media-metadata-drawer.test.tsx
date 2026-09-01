import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchJson, type MetadataLinkPreview, type MetadataSearchResult } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { MediaMetadataDrawer } from "./media-metadata-drawer";

vi.mock("../../lib/api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../lib/api")>(),
  fetchJson: vi.fn()
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: vi.fn() }));

const match: MetadataSearchResult = {
  provider: "tmdb",
  providerId: "42",
  mediaType: "movies",
  title: "Correct Movie",
  originalTitle: null,
  year: 2021,
  overview: "The correct provider record.",
  posterUrl: null,
  backdropUrl: null,
  rating: 7,
  ratings: [],
  genres: ["Drama"],
  cast: [],
  crew: [],
  imdbId: "tt0000042",
  externalUrl: "https://www.themoviedb.org/movie/42"
};

const preview: MetadataLinkPreview = {
  mediaType: "movies",
  subjectId: "movie-1",
  current: {
    provider: "tmdb",
    providerId: "1",
    title: "Wrong Movie",
    year: 2020,
    imdbId: "tt0000001",
    context: "Old Collection"
  },
  proposed: {
    provider: "tmdb",
    providerId: "42",
    title: "Correct Movie",
    year: 2021,
    imdbId: "tt0000042",
    context: "New Collection"
  },
  changes: [
    "Provider record: 1 → 42",
    "Title: Wrong Movie → Correct Movie",
    "Year: 2020 → 2021",
    "IMDb ID: tt0000001 → tt0000042"
  ],
  consequences: [
    "Imported files, edition and release facts, monitoring, history, tags, and plan assignments will be kept.",
    "Collection will change from Old Collection to New Collection."
  ],
  conflict: null,
  catalogueImpact: null,
  canApply: true,
  blockReason: null,
  confirmationToken: "PREVIEW-TOKEN"
};

describe("MediaMetadataDrawer remap review", () => {
  beforeEach(() => vi.resetAllMocks());

  it("previews identity and consequences before a keyboard-applied remap", async () => {
    const user = userEvent.setup();
    const onChanged = vi.fn();
    vi.mocked(fetchJson).mockResolvedValue([match]);
    vi.mocked(authedFetch)
      .mockResolvedValueOnce(Response.json(preview))
      .mockResolvedValueOnce(Response.json({ id: "movie-1", title: "Correct Movie" }));

    renderDrawer(onChanged);
    await user.click(screen.getByRole("button", { name: "Find" }));
    const previewButton = screen.getByRole("button", { name: "Preview" });
    previewButton.focus();
    await user.keyboard("{Enter}");

    const region = await screen.findByRole("region", { name: "Metadata remap preview" });
    expect(region).toBeVisible();
    expect(region.querySelector(".sm\\:grid-cols-2")).not.toBeNull();
    expect(within(region).getByText("Wrong Movie (2020)")).toBeVisible();
    expect(within(region).getByText("Correct Movie (2021)")).toBeVisible();
    expect(within(region).getByText(/Title: Wrong Movie → Correct Movie/)).toBeVisible();
    expect(within(region).getByText(/edition and release facts.*will be kept/i)).toBeVisible();
    expect(within(region).getByText(/collection will change/i)).toBeVisible();

    const apply = within(region).getByRole("button", { name: "Apply remap" });
    apply.focus();
    await user.keyboard("{Enter}");

    expect(authedFetch).toHaveBeenNthCalledWith(
      1,
      "/api/movies/movie-1/metadata/link/preview",
      expect.objectContaining({ body: JSON.stringify({ providerId: "42" }) })
    );
    expect(authedFetch).toHaveBeenNthCalledWith(
      2,
      "/api/movies/movie-1/metadata/link",
      expect.objectContaining({
        body: JSON.stringify({ providerId: "42", confirmationToken: "PREVIEW-TOKEN" })
      })
    );
    expect(onChanged).toHaveBeenCalledOnce();
  });

  it("shows a held-title conflict and never enables apply", async () => {
    const user = userEvent.setup();
    vi.mocked(fetchJson).mockResolvedValue([match]);
    vi.mocked(authedFetch).mockResolvedValueOnce(Response.json({
      ...preview,
      conflict: { id: "movie-2", title: "Already Held", reason: "provider-id" },
      canApply: false,
      blockReason: "Already Held already owns the proposed provider record. Deluno will not merge or duplicate the two movies."
    } satisfies MetadataLinkPreview));

    renderDrawer(vi.fn());
    await user.click(screen.getByRole("button", { name: "Find" }));
    await user.click(screen.getByRole("button", { name: "Preview" }));

    const region = await screen.findByRole("region", { name: "Metadata remap preview" });
    expect(within(region).getByText(/will not merge or duplicate/i)).toBeVisible();
    expect(within(region).getByRole("button", { name: "Apply remap" })).toBeDisabled();
    expect(authedFetch).toHaveBeenCalledTimes(1);
  });
});

function renderDrawer(onChanged: () => void) {
  return render(
    <MediaMetadataDrawer
      open
      onOpenChange={vi.fn()}
      endpointBase="/api/movies/movie-1"
      mediaType="movies"
      mediaLabel="movie"
      title="Wrong Movie"
      year={2020}
      provider="tmdb"
      providerId="1"
      posterUrl={null}
      externalUrl={null}
      value={{
        originalTitle: "",
        overview: "Stored overview",
        posterUrl: "",
        backdropUrl: "",
        rating: "",
        genres: "",
        externalUrl: "",
        imdbId: "tt0000001"
      }}
      onChanged={onChanged}
    />
  );
}
