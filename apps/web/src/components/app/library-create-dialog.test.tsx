import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createRef } from "react";
import type { ComponentProps } from "react";
import type { Dispatch, SetStateAction } from "react";
import { describe, expect, it, vi } from "vitest";
import type { MetadataSearchResult } from "../../lib/api";
import type { CreateFormDraft, LibraryVariant } from "../../hooks/use-library-create";
import { LibraryCreateDialog } from "./library-create-dialog";

vi.mock("../../lib/api", () => ({
  fetchJson: vi.fn(async (url: string) => {
    if (url === "/api/tags") return [{ id: "t1", name: "kids", color: "slate", description: "", createdUtc: "", updatedUtc: "" }];
    return [];
  })
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

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
  variant: LibraryVariant = "movies",
  createFormOverrides: Partial<CreateFormDraft> = {},
  extra: Partial<ComponentProps<typeof LibraryCreateDialog>> = {}
) {
  const onSelectResult = vi.fn();
  const onOpenHeldResult = vi.fn();
  const setCreateForm = vi.fn<Dispatch<SetStateAction<CreateFormDraft>>>();

  render(
    <LibraryCreateDialog
      open
      onOpenChange={vi.fn()}
      variant={variant}
      label={variant === "movies" ? "movies" : "shows"}
      singular={variant === "movies" ? "movie" : "show"}
      metadataStatus={null}
      isCreating={false}
      createForm={{
        title: "arrival",
        year: "",
        imdbId: "",
        monitored: true,
        monitorPreset: "all",
        searchNow: true,
        qualityProfileId: "",
        tags: "",
        metadata: null,
        ...createFormOverrides
      }}
      setCreateForm={setCreateForm}
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
      {...extra}
    />
  );

  return { onSelectResult, onOpenHeldResult, setCreateForm };
}

/**
 * Monitor presets (#991): only shows have episodes to pick among, and the
 * choice only matters while the show is actually going to be monitored.
 */
describe("the add-show monitor preset", () => {
  it("is offered for shows", () => {
    renderDialog([], "shows");
    expect(screen.getByLabelText("Which episodes to monitor")).toBeInTheDocument();
  });

  it("is not offered for movies, which have no episodes to pick among", () => {
    renderDialog([], "movies");
    expect(screen.queryByLabelText("Which episodes to monitor")).not.toBeInTheDocument();
  });

  it("is hidden once the show is set to not monitor at all", () => {
    renderDialog([], "shows", { monitored: false });
    expect(screen.queryByLabelText("Which episodes to monitor")).not.toBeInTheDocument();
  });

  it("offers every named preset from the owner's decision (#991)", () => {
    renderDialog([], "shows");
    const select = screen.getByLabelText("Which episodes to monitor") as HTMLSelectElement;
    const labels = [...select.options].map((option) => option.textContent);
    expect(labels).toEqual([
      "All episodes",
      "Future episodes only",
      "Missing episodes only",
      "First season only",
      "Latest season only",
      "Pilot only",
      "None"
    ]);
  });

  it("changing the preset updates only that field", async () => {
    const user = userEvent.setup();
    const { setCreateForm } = renderDialog([], "shows");

    await user.selectOptions(screen.getByLabelText("Which episodes to monitor"), "pilot");

    expect(setCreateForm).toHaveBeenCalledTimes(1);
    const updater = setCreateForm.mock.calls[0][0] as (
      current: { monitorPreset: string; monitored: boolean }
    ) => unknown;
    expect(updater({ monitorPreset: "all", monitored: true })).toMatchObject({
      monitorPreset: "pilot",
      monitored: true
    });
  });
});

/** Tags on add (#1038): an optional picker whose value is the form's tags. */
describe("the add dialog tag field", () => {
  it("is offered for movies", () => {
    renderDialog([], "movies");
    expect(screen.getByLabelText("Tags for the movie being added")).toBeInTheDocument();
  });

  it("is offered for shows", () => {
    renderDialog([], "shows");
    expect(screen.getByLabelText("Tags for the show being added")).toBeInTheDocument();
  });

  it("shows the tags the form already carries, such as the ones remembered for the library", () => {
    renderDialog([], "movies", { tags: "kids" });
    expect(screen.getByText("kids")).toBeInTheDocument();
  });
});

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

  it("labels a row by who described it: TheTVDB where it did, otherwise TMDb", () => {
    renderDialog(
      [
        result({ title: "From TheTVDB", mediaType, dataSource: "tvdb" }),
        result({ title: "From TMDb", mediaType, providerId: "273481", imdbId: "tt3397884" })
      ],
      variant
    );

    expect(screen.getByTitle("Select From TheTVDB")).toHaveTextContent("TheTVDB");
    expect(screen.getByTitle("Select From TMDb")).toHaveTextContent("· TMDb");
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

/**
 * Whether an add searches at once is one clear choice (#1145). It used to be
 * two boxes beside a monitor box that searched anyway; one box now says what it
 * does, and the library and quality profile are picked before anything runs.
 */
describe("the search-on-add choice", () => {
  it("is one box, ticked by default, and the old second box is gone", () => {
    renderDialog([]);

    expect(screen.getByRole("checkbox", { name: "Search for it as soon as it is added" })).toBeChecked();
    expect(screen.queryByRole("checkbox", { name: "Search for missing now" })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: "Search for cutoff unmet now" })).not.toBeInTheDocument();
  });

  it("unticking it changes only that field", async () => {
    const user = userEvent.setup();
    const { setCreateForm } = renderDialog([]);

    await user.click(screen.getByRole("checkbox", { name: "Search for it as soon as it is added" }));

    expect(setCreateForm).toHaveBeenCalledTimes(1);
    const updater = setCreateForm.mock.calls[0][0] as unknown as (current: { searchNow: boolean; monitored: boolean }) => unknown;
    expect(updater({ searchNow: true, monitored: true })).toMatchObject({ searchNow: false, monitored: true });
  });
});

describe("the library and quality profile choice", () => {
  const libraries = [
    { id: "lib-1080", name: "Movies 1080p", qualityProfileId: "p-1080" },
    { id: "lib-4k", name: "Movies 4K", qualityProfileId: "p-4k" }
  ];
  const qualityProfiles = [
    { id: "p-1080", name: "Movies / 1080p" },
    { id: "p-4k", name: "Movies / 4K Streaming" }
  ];

  it("offers the library and starts the quality profile as that library's own", () => {
    renderDialog([], "movies", {}, { libraries, libraryId: "lib-4k", onLibraryChange: vi.fn(), qualityProfiles });

    expect(screen.getByLabelText("Library")).toHaveValue("lib-4k");
    expect(screen.getByLabelText("Quality profile")).toHaveValue("p-4k");
  });

  it("is offered even when there is only one library", () => {
    renderDialog([], "movies", {}, { libraries: [libraries[0]], libraryId: "lib-1080", onLibraryChange: vi.fn(), qualityProfiles });

    expect(screen.getByLabelText("Library")).toBeInTheDocument();
    expect(screen.getByLabelText("Quality profile")).toHaveValue("p-1080");
  });

  it("picking another profile is remembered on the form, and picking the library's own clears it", async () => {
    const user = userEvent.setup();
    const { setCreateForm } = renderDialog([], "movies", {}, { libraries, libraryId: "lib-1080", onLibraryChange: vi.fn(), qualityProfiles });

    await user.selectOptions(screen.getByLabelText("Quality profile"), "p-4k");
    const picked = setCreateForm.mock.calls[0][0] as unknown as (current: { qualityProfileId: string }) => { qualityProfileId: string };
    expect(picked({ qualityProfileId: "" }).qualityProfileId).toBe("p-4k");

    setCreateForm.mockClear();
    await user.selectOptions(screen.getByLabelText("Quality profile"), "p-1080");
    const own = setCreateForm.mock.calls[0][0] as unknown as (current: { qualityProfileId: string }) => { qualityProfileId: string };
    expect(own({ qualityProfileId: "p-4k" }).qualityProfileId).toBe("");
  });
});
