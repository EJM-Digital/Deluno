import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createRef } from "react";
import { describe, expect, it, vi } from "vitest";
import type { MetadataSearchResult } from "../../lib/api";
import type { LibraryVariant } from "../../hooks/use-library-create";
import { LibraryCreateDialog } from "./library-create-dialog";

/**
 * The Add screen tells you when you already have something (#424).
 *
 * Deluno has never added a title twice — the catalogue collapses a second add
 * onto the row it already holds. What it never did was say so, so a search for
 * a film sitting in the library offered the same "Click to select" as a film
 * that was not. These hold the telling, and hold it for shows as well as films:
 * the two screens are one component, and a marker that only worked for films
 * would be a bug that only showed up in TV.
 */

function result(overrides: Partial<MetadataSearchResult> = {}): MetadataSearchResult {
  return {
    provider: "tmdb",
    providerId: "329865",
    mediaType: "movies",
    title: "Arrival",
    originalTitle: null,
    year: 2016,
    overview: null,
    posterUrl: null,
    backdropUrl: null,
    rating: null,
    ratings: [],
    genres: [],
    cast: [],
    crew: [],
    imdbId: "tt2543164",
    externalUrl: null,
    ...overrides
  };
}

function renderDialog(
  metadataResults: MetadataSearchResult[],
  variant: LibraryVariant = "movies"
) {
  const onSelectResult = vi.fn();
  const onOpenHeldResult = vi.fn();

  render(
    <LibraryCreateDialog
      open
      onOpenChange={vi.fn()}
      variant={variant}
      label={variant === "movies" ? "movies" : "shows"}
      singular={variant === "movies" ? "movie" : "show"}
      metadataStatus={null}
      isCreating={false}
      createForm={{ title: "arrival", year: "", imdbId: "", monitored: true, metadata: null }}
      setCreateForm={vi.fn()}
      metadataResults={metadataResults}
      setMetadataResults={vi.fn()}
      selectedMetadataResults={[]}
      setSelectedMetadataResults={vi.fn()}
      isSearchingMetadata={false}
      metadataSearchSequence={createRef<number>() as never}
      onSearch={vi.fn()}
      onSelectResult={onSelectResult}
      onOpenHeldResult={onOpenHeldResult}
      onCreate={vi.fn()}
    />
  );

  return { onSelectResult, onOpenHeldResult };
}

describe.each([
  { variant: "movies" as const, mediaType: "movies", held: "Arrival", other: "Sicario" },
  { variant: "shows" as const, mediaType: "tv", held: "Severance", other: "Silo" }
])("the add dialog for $variant", ({ variant, mediaType, held, other }) => {
  it("says which result you already have, and offers to open it", () => {
    renderDialog(
      [
        result({ title: held, mediaType, libraryEntryId: "entry-1" }),
        result({ title: other, mediaType, providerId: "273481", imdbId: "tt3397884" })
      ],
      variant
    );

    expect(screen.getByTitle(`Open ${held} — already in your library`)).toBeInTheDocument();
    expect(screen.getByText(/Already in your library/)).toBeInTheDocument();

    // And says nothing of the kind about the one it does not hold.
    expect(screen.getByTitle(`Select ${other}`)).toBeInTheDocument();
    expect(screen.getAllByText("Click to select")).toHaveLength(1);
  });

  it("opens a held title instead of selecting it for a second add", async () => {
    const user = userEvent.setup();
    const { onSelectResult, onOpenHeldResult } = renderDialog(
      [result({ title: held, mediaType, libraryEntryId: "entry-1" })],
      variant
    );

    await user.click(screen.getByTitle(`Open ${held} — already in your library`));

    expect(onOpenHeldResult).toHaveBeenCalledTimes(1);
    expect(onOpenHeldResult.mock.calls[0][0]).toMatchObject({ title: held, libraryEntryId: "entry-1" });
    expect(onSelectResult).not.toHaveBeenCalled();
  });

  it("still selects a title you do not have", async () => {
    const user = userEvent.setup();
    const { onSelectResult, onOpenHeldResult } = renderDialog(
      [result({ title: other, mediaType, providerId: "273481" })],
      variant
    );

    await user.click(screen.getByTitle(`Select ${other}`));

    expect(onSelectResult).toHaveBeenCalledTimes(1);
    expect(onOpenHeldResult).not.toHaveBeenCalled();
  });

  /**
   * A null id is what the server sends for a title it does not hold. Treating
   * it as held would send the reader to `/movies/null`.
   */
  it("treats a null library entry as not held", () => {
    renderDialog([result({ title: other, mediaType, libraryEntryId: null })], variant);

    expect(screen.getByTitle(`Select ${other}`)).toBeInTheDocument();
    expect(screen.queryByText(/Already in your library/)).not.toBeInTheDocument();
  });
});
