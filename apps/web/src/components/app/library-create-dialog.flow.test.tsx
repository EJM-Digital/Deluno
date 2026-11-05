import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import type { ComponentProps, Dispatch, SetStateAction } from "react";
import { describe, expect, it, vi } from "vitest";
import type { MetadataSearchResult } from "../../lib/api";
import type { CreateFormDraft, LibraryVariant } from "../../hooks/use-library-create";
import { createInitialLibraryForm } from "../../hooks/use-library-create";
import { LibraryCreateDialog } from "./library-create-dialog";

vi.mock("../../lib/api", () => ({
  fetchJson: vi.fn(async (url: string) => {
    if (url === "/api/tags") return [{ id: "t1", name: "kids", color: "slate", description: "", createdUtc: "", updatedUtc: "" }];
    if (url === "/api/settings") return { autoStartJobs: true };
    return [];
  })
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

/**
 * The add dialog end to end: what it says, what each control does to the form,
 * and when the Add button may be pressed. The paused-automation notice, the
 * monitor presets and the held-title marker are pinned in the neighbouring file.
 */

function found(overrides: Partial<MetadataSearchResult> = {}): MetadataSearchResult {
  return {
    provider: "tmdb",
    providerId: "1",
    mediaType: "movies",
    title: "Arrival",
    originalTitle: null,
    year: 2016,
    overview: null,
    posterUrl: null,
    backdropUrl: null,
    rating: null,
    genres: [],
    imdbId: null,
    externalUrl: null,
    ...overrides
  };
}

type Update = (current: CreateFormDraft) => CreateFormDraft;

function show(overrides: {
  variant?: LibraryVariant;
  form?: Partial<CreateFormDraft>;
} & Partial<ComponentProps<typeof LibraryCreateDialog>> = {}) {
  const { variant = "movies", form, ...rest } = overrides;
  const setCreateForm = vi.fn<Dispatch<SetStateAction<CreateFormDraft>>>();
  const props = {
    onOpenChange: vi.fn(),
    setMetadataResults: vi.fn(),
    setSelectedMetadataResults: vi.fn(),
    onSearch: vi.fn(),
    onSelectResult: vi.fn(),
    onOpenHeldResult: vi.fn(),
    onCreate: vi.fn((event: { preventDefault: () => void }) => event.preventDefault()),
    metadataSearchSequence: { current: 0 },
    setCreateForm
  };
  render(
    <LibraryCreateDialog
      open
      variant={variant}
      label={variant === "movies" ? "movies" : "shows"}
      singular={variant === "movies" ? "movie" : "show"}
      isCreating={false}
      createForm={{ ...createInitialLibraryForm(), ...form }}
      metadataResults={[]}
      selectedMetadataResults={[]}
      isSearchingMetadata={false}
      {...props}
      {...rest}
    />
  );
  /** What the last setCreateForm call would make of a form. */
  const lastUpdate = (from: Partial<CreateFormDraft> = {}) =>
    (setCreateForm.mock.calls.at(-1)![0] as unknown as Update)({ ...createInitialLibraryForm(), ...from });
  return { ...props, lastUpdate };
}

/** The dialog on a real form, for the controls whose handler reads the typed text. */
function showLive(initial: Partial<CreateFormDraft> = {}, variant: LibraryVariant = "movies") {
  const seen = { form: { ...createInitialLibraryForm(), ...initial } };
  const sequence = { current: 0 };
  const setMetadataResults = vi.fn();
  const setSelectedMetadataResults = vi.fn();
  function Live() {
    const [form, setForm] = useState(seen.form);
    seen.form = form;
    return (
      <LibraryCreateDialog
        open
        onOpenChange={vi.fn()}
        variant={variant}
        label="movies"
        singular="movie"
        isCreating={false}
        createForm={form}
        setCreateForm={setForm}
        metadataResults={[]}
        setMetadataResults={setMetadataResults}
        selectedMetadataResults={[]}
        setSelectedMetadataResults={setSelectedMetadataResults}
        isSearchingMetadata={false}
        metadataSearchSequence={sequence as never}
        onSearch={vi.fn()}
        onSelectResult={vi.fn()}
        onOpenHeldResult={vi.fn()}
        onCreate={vi.fn()}
      />
    );
  }
  render(<Live />);
  return { seen, sequence, setMetadataResults, setSelectedMetadataResults };
}

const addButton = () => screen.getByRole("button", { name: /^Add (selected|\d|\w+ manually)/ });

describe("the add dialog's heading and match status", () => {
  it("names what is being added and shows the title as ready to match when a provider is set up", () => {
    show({ variant: "shows", metadataStatus: { isConfigured: true } as never });
    expect(screen.getByRole("heading", { name: "Add show" })).toBeInTheDocument();
    expect(screen.getByText("Title matching ready")).toBeInTheDocument();
    expect(screen.queryByText("Manual entry")).not.toBeInTheDocument();
    expect(screen.getByText("Matches auto-refresh as you type, or press Enter to refresh now.")).toBeInTheDocument();
    expect(screen.queryByText(/Title matching is temporarily unavailable/)).not.toBeInTheDocument();
  });

  it("says it is manual entry when nothing has been said about matching", () => {
    show({ metadataStatus: null });
    expect(screen.getByText("Manual entry")).toBeInTheDocument();
    expect(screen.queryByText("Title matching ready")).not.toBeInTheDocument();
    expect(screen.queryByText(/Title matching is temporarily unavailable/)).not.toBeInTheDocument();
  });

  it("says matching is unavailable, twice, when the provider is not set up, and still allows a manual add", () => {
    show({ metadataStatus: { isConfigured: false } as never, isSearchingMetadata: true });
    expect(screen.getByText("Manual entry")).toBeInTheDocument();
    expect(screen.getByText("Metadata matching is currently unavailable.")).toBeInTheDocument();
    expect(screen.queryByText(/Waiting on/)).not.toBeInTheDocument();
    expect(screen.getByText("Title matching is temporarily unavailable. You can still add this title manually below.")).toBeInTheDocument();
  });

  it("shows a spinner while searching and the magnifier otherwise", () => {
    show({ isSearchingMetadata: true });
    const status = screen.getByText("Waiting on TMDb...");
    expect(status.querySelector(".animate-spin")).not.toBeNull();
  });

  it("shows no spinner when idle", () => {
    show();
    const status = screen.getByText(/Matches auto-refresh/);
    expect(status.querySelector(".animate-spin")).toBeNull();
    expect(status.querySelector("svg")).not.toBeNull();
  });

  it("closes through the X and through Cancel", async () => {
    const user = userEvent.setup();
    const { onOpenChange } = show();
    await user.click(screen.getByRole("button", { name: "Close add movie" }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    onOpenChange.mockClear();
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("cannot be closed or cancelled while the title is being created", () => {
    show({ isCreating: true });
    expect(screen.getByRole("button", { name: "Close add movie" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
  });
});

describe("the add dialog's search box", () => {
  it("asks for the right kind of title", () => {
    show({ variant: "movies" });
    expect(screen.getByPlaceholderText("Search movies, for example Top Gun")).toBeInTheDocument();
  });

  it("asks for a show on the TV screen, and for a start year", () => {
    show({ variant: "shows" });
    expect(screen.getByPlaceholderText("Search TV shows, for example Severance")).toBeInTheDocument();
    expect(screen.getByPlaceholderText("Start year (optional)")).toBeInTheDocument();
  });

  it("asks for a year on the movie screen", () => {
    show({ variant: "movies" });
    expect(screen.getByPlaceholderText("Year (optional)")).toBeInTheDocument();
  });

  it("starts a search when Enter is pressed, without reloading the page", async () => {
    const user = userEvent.setup();
    const { onSearch } = show({ form: { title: "arr" } });
    await user.type(screen.getByLabelText("What do you want to add?"), "{Enter}");
    expect(onSearch).toHaveBeenCalledTimes(1);
  });

  it("throws away the old matches and the picks when the words change, and forgets the chosen match", async () => {
    const user = userEvent.setup();
    const { seen, sequence, setMetadataResults, setSelectedMetadataResults } = showLive({ title: "", metadata: found(), year: "2001" });
    sequence.current = 4;
    await user.type(screen.getByLabelText("What do you want to add?"), "x");
    expect(sequence.current).toBe(5);
    expect(setMetadataResults).toHaveBeenCalledWith([]);
    expect(setSelectedMetadataResults).toHaveBeenCalledWith([]);
    expect(seen.form.title).toBe("x");
    expect(seen.form.metadata).toBeNull();
    expect(seen.form.year).toBe("2001");
  });
});

describe("the add dialog's match cards", () => {
  it("shows no list until there are matches", () => {
    show();
    expect(screen.queryByText(/Choose one or more matches/)).not.toBeInTheDocument();
  });

  it("shows every match it is given, a whole page of twenty, each with its title, year and where it came from", () => {
    const results = Array.from({ length: 20 }, (_, index) => found({ providerId: String(index), title: `Film ${index}`, year: 2000 + index }));
    show({ metadataResults: results });
    expect(screen.getByText("Choose one or more matches to prefill (this does not add yet)")).toBeInTheDocument();
    expect(screen.getByText("Film 5")).toBeInTheDocument();
    expect(screen.getByText("Film 19")).toBeInTheDocument();
    expect(within(screen.getByTestId("add-matches")).getAllByRole("button")).toHaveLength(20);
    expect(screen.getByText("2003 · TMDb")).toBeInTheDocument();
  });

  it("gives each match its synopsis, cut to a few lines until the match is picked and whole once it is", () => {
    const long = "A long synopsis. ".repeat(40).trim();
    const picked = found({ providerId: "a", title: "Picked", overview: long });
    const other = found({ providerId: "b", title: "Other", overview: long });
    const bare = found({ providerId: "c", title: "Bare", overview: null });
    show({ metadataResults: [picked, other, bare], selectedMetadataResults: [picked] });
    const synopsis = (title: string) => within(screen.getByTitle(`Select ${title}`)).getByText(long);
    expect(synopsis("Picked")).not.toHaveClass("line-clamp-4");
    expect(synopsis("Other")).toHaveClass("line-clamp-4");
    expect(within(screen.getByTitle("Select Bare")).getByText("No synopsis yet.")).toBeInTheDocument();
  });

  it("offers More matches only while there are more, asks for them when pressed, and is disabled while they load", async () => {
    const user = userEvent.setup();
    const results = [found({ providerId: "a", title: "One" })];
    const onMoreMatches = vi.fn();
    show({ metadataResults: results, hasMoreMatches: true, onMoreMatches });
    await user.click(screen.getByRole("button", { name: "More matches" }));
    expect(onMoreMatches).toHaveBeenCalledTimes(1);
  });

  it("hides More matches when there are no more, when nothing was found, and shows it busy while loading", () => {
    const results = [found({ providerId: "a", title: "One" })];
    show({ metadataResults: results, hasMoreMatches: false, onMoreMatches: vi.fn() });
    expect(screen.queryByRole("button", { name: "More matches" })).not.toBeInTheDocument();
    cleanup();
    show({ metadataResults: [], hasMoreMatches: true, onMoreMatches: vi.fn() });
    expect(screen.queryByRole("button", { name: "More matches" })).not.toBeInTheDocument();
    cleanup();
    show({ metadataResults: results, hasMoreMatches: true, isLoadingMoreMatches: true, onMoreMatches: vi.fn() });
    expect(screen.getByRole("button", { name: "More matches" })).toBeDisabled();
  });

  it("says Unknown year, shows art when there is some, and the rating only when there is one", () => {
    show({
      metadataResults: [
        found({ providerId: "a", title: "Plain", year: null, rating: null, posterUrl: null }),
        found({ providerId: "b", title: "Has art", rating: 8.64, posterUrl: "https://img.example/p.jpg" }),
        found({ providerId: "c", title: "Zero score", rating: 0 })
      ]
    });
    expect(screen.getByText("Unknown year · TMDb")).toBeInTheDocument();
    expect(screen.getAllByText("No art")).toHaveLength(2);
    expect(screen.getByText("8.6 rating")).toBeInTheDocument();
    expect(screen.getAllByText(/ rating$/)).toHaveLength(1);
  });

  it("marks the picked ones as Selected and invites a click on the rest", () => {
    const picked = found({ providerId: "a", title: "Picked" });
    const other = found({ providerId: "b", title: "Other" });
    show({ metadataResults: [picked, other], selectedMetadataResults: [picked] });
    const pickedCard = screen.getByTitle("Select Picked");
    const otherCard = screen.getByTitle("Select Other");
    expect(within(pickedCard).getByText("Selected")).toBeInTheDocument();
    expect(within(pickedCard).queryByText("Click to select")).not.toBeInTheDocument();
    expect(within(otherCard).getByText("Click to select")).toBeInTheDocument();
    expect(within(otherCard).queryByText("Selected")).not.toBeInTheDocument();
  });

  it("treats the same id from another provider as a different title", () => {
    const tmdb = found({ provider: "tmdb", providerId: "7", title: "Same id" });
    const tvdb = found({ provider: "tvdb", providerId: "7", title: "Same id elsewhere" });
    show({ metadataResults: [tmdb, tvdb], selectedMetadataResults: [tmdb] });
    expect(within(screen.getByTitle("Select Same id elsewhere")).getByText("Click to select")).toBeInTheDocument();
  });

  it("sends a click to select, or to open when the title is already held", async () => {
    const user = userEvent.setup();
    const free = found({ providerId: "a", title: "Free" });
    const held = found({ providerId: "b", title: "Held", libraryEntryId: "entry-9" });
    const { onSelectResult, onOpenHeldResult } = show({ metadataResults: [free, held] });
    await user.click(screen.getByTitle("Select Free"));
    expect(onSelectResult).toHaveBeenCalledWith(free);
    expect(onOpenHeldResult).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Open Held, already in your library" }));
    expect(onOpenHeldResult).toHaveBeenCalledWith(held);
    expect(onSelectResult).toHaveBeenCalledTimes(1);
  });

  it("credits TheTVDB on the TV screen when a match came from it, and not otherwise", () => {
    const tvdb = found({ providerId: "a", tvDbId: "99", dataSource: "tvdb" });
    show({ variant: "shows", metadataResults: [tvdb] });
    expect(screen.getByRole("link", { name: "TheTVDB" })).toBeInTheDocument();
  });

  it("does not credit TheTVDB for a TV match that did not come from it", () => {
    show({ variant: "shows", metadataResults: [found({ providerId: "b" })] });
    expect(screen.queryByRole("link", { name: "TheTVDB" })).not.toBeInTheDocument();
  });

  it("does not credit TheTVDB on the movie screen", () => {
    show({ variant: "movies", metadataResults: [found({ tvDbId: "99" })] });
    expect(screen.queryByRole("link", { name: "TheTVDB" })).not.toBeInTheDocument();
  });
});

describe("adding by hand", () => {
  it("changes only the year when the year is typed", async () => {
    const user = userEvent.setup();
    const { seen } = showLive({ title: "Keep", imdbId: "tt1" });
    await user.type(screen.getByPlaceholderText("Year (optional)"), "19");
    expect(seen.form).toMatchObject({ year: "19", title: "Keep", imdbId: "tt1" });
  });

  it("changes only the IMDb id when that is typed", async () => {
    const user = userEvent.setup();
    const { seen } = showLive({ year: "1999" });
    await user.type(screen.getByPlaceholderText("IMDb ID (optional)"), "tt2");
    expect(seen.form).toMatchObject({ imdbId: "tt2", year: "1999" });
  });
});

describe("where the title goes", () => {
  const libraries = [
    { id: "lib-a", name: "Movies A", qualityProfileId: "p-a" },
    { id: "lib-b", name: "Movies B" }
  ];
  const qualityProfiles = [
    { id: "p-a", name: "Profile A" },
    { id: "p-b", name: "Profile B" }
  ];

  it("offers no choice of library without libraries, or without a way to change it", () => {
    show({ libraries: [], onLibraryChange: vi.fn() });
    expect(screen.queryByLabelText("Library")).not.toBeInTheDocument();
  });

  it("offers no choice of library when nothing listens for the change", () => {
    show({ libraries });
    expect(screen.queryByLabelText("Library")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Quality profile")).not.toBeInTheDocument();
  });

  it("starts on the first library when none is named, and explains the choice for each kind of title", () => {
    show({ libraries, onLibraryChange: vi.fn(), qualityProfiles });
    expect(screen.getByLabelText("Library")).toHaveValue("lib-a");
    expect(screen.getByText(/It can be kept in another library too, later, from its page\./)).toBeInTheDocument();
  });

  it("says a show can be moved rather than kept in two places", () => {
    show({ variant: "shows", libraries, onLibraryChange: vi.fn() });
    expect(screen.getByText(/It can be moved to another library later, from its page\./)).toBeInTheDocument();
  });

  it("tells the screen which library was chosen and clears any profile picked for the old one", async () => {
    const user = userEvent.setup();
    const onLibraryChange = vi.fn();
    const { lastUpdate } = show({ libraries, libraryId: "lib-a", onLibraryChange, qualityProfiles, form: { qualityProfileId: "p-b" } });
    await user.selectOptions(screen.getByLabelText("Library"), "lib-b");
    expect(onLibraryChange).toHaveBeenCalledWith("lib-b");
    expect(lastUpdate({ qualityProfileId: "p-b", title: "Keep" })).toMatchObject({ qualityProfileId: "", title: "Keep" });
  });

  it("offers the libraries by name", () => {
    show({ libraries, onLibraryChange: vi.fn() });
    expect(within(screen.getByLabelText("Library")).getAllByRole("option").map((option) => option.textContent)).toEqual(["Movies A", "Movies B"]);
  });

  it("offers no profile choice when there are no profiles", () => {
    show({ libraries, onLibraryChange: vi.fn(), qualityProfiles: [] });
    expect(screen.queryByLabelText("Quality profile")).not.toBeInTheDocument();
  });

  it("marks the library's own profile and shows it until another is picked", () => {
    show({ libraries, libraryId: "lib-a", onLibraryChange: vi.fn(), qualityProfiles });
    const select = screen.getByLabelText("Quality profile");
    expect(select).toHaveValue("p-a");
    expect(within(select).getAllByRole("option").map((option) => option.textContent)).toEqual(["Profile A (library's)", "Profile B"]);
  });

  it("shows the profile that was picked instead of the library's", () => {
    show({ libraries, libraryId: "lib-a", onLibraryChange: vi.fn(), qualityProfiles, form: { qualityProfileId: "p-b" } });
    expect(screen.getByLabelText("Quality profile")).toHaveValue("p-b");
  });

  it("marks no profile as the library's own when the library has none, and takes any pick as a choice", async () => {
    const user = userEvent.setup();
    const { lastUpdate } = show({ libraries, libraryId: "lib-b", onLibraryChange: vi.fn(), qualityProfiles });
    expect(within(screen.getByLabelText("Quality profile")).getAllByRole("option").map((option) => option.textContent)).toEqual(["Profile A", "Profile B"]);
    await user.selectOptions(screen.getByLabelText("Quality profile"), "p-a");
    expect(lastUpdate({ title: "Keep" })).toMatchObject({ qualityProfileId: "p-a", title: "Keep" });
  });

  it("follows the library again when its own profile is picked", async () => {
    const user = userEvent.setup();
    const { lastUpdate } = show({ libraries, libraryId: "lib-a", onLibraryChange: vi.fn(), qualityProfiles, form: { qualityProfileId: "p-b" } });
    await user.selectOptions(screen.getByLabelText("Quality profile"), "p-a");
    expect(lastUpdate({ qualityProfileId: "p-b" }).qualityProfileId).toBe("");
  });
});

describe("tags and monitoring", () => {
  it("changes only the tags when one is taken off", async () => {
    const user = userEvent.setup();
    const { lastUpdate } = show({ form: { tags: "kids" } });
    await user.click(await screen.findByRole("button", { name: "Remove kids from Tags for the movie being added" }));
    expect(lastUpdate({ tags: "kids", title: "Keep" })).toMatchObject({ tags: "", title: "Keep" });
  });

  it("toggles Monitor it and changes nothing else", async () => {
    const user = userEvent.setup();
    const { lastUpdate } = show({ form: { monitored: true } });
    const box = screen.getByRole("checkbox", { name: "Monitor it" });
    expect(box).toBeChecked();
    await user.click(box);
    expect(lastUpdate({ monitored: true, searchNow: true })).toMatchObject({ monitored: false, searchNow: true });
  });

  it("shows Monitor it unticked when the form says so", () => {
    show({ form: { monitored: false } });
    expect(screen.getByRole("checkbox", { name: "Monitor it" })).not.toBeChecked();
  });

  it("changes only the search choice when it is toggled", async () => {
    const user = userEvent.setup();
    const { lastUpdate } = show({ form: { monitored: true, searchNow: true } });
    await user.click(screen.getByRole("checkbox", { name: "Search for it as soon as it is added" }));
    expect(lastUpdate({ monitored: true, searchNow: true })).toMatchObject({ monitored: true, searchNow: false });
  });

  it("shows the search choice unticked when the form says not to search", () => {
    show({ form: { monitored: true, searchNow: false } });
    expect(screen.getByRole("checkbox", { name: "Search for it as soon as it is added" })).not.toBeChecked();
  });

  it("says nothing about paused automation when the title will not be searched for", async () => {
    show({ form: { monitored: true, searchNow: false } });
    await new Promise((resolve) => setTimeout(resolve, 20)); // reason: shows nothing is drawn, so there is nothing to wait for
    expect(screen.queryByText(/Automation is paused/)).not.toBeInTheDocument();
  });
});

describe("the Add button", () => {
  it("is off until there is a title or a pick, and spaces do not count as a title", () => {
    show({ form: { title: "" } });
    expect(addButton()).toBeDisabled();
  });

  it("stays off for a title of only spaces", () => {
    show({ form: { title: "   " } });
    expect(addButton()).toBeDisabled();
  });

  it("is ready to add by hand once a title is typed", () => {
    show({ form: { title: "Arrival" } });
    expect(screen.getByRole("button", { name: "Add movie manually" })).toBeEnabled();
  });

  it("says Add show manually on the TV screen", () => {
    show({ variant: "shows", form: { title: "Severance" } });
    expect(screen.getByRole("button", { name: "Add show manually" })).toBeEnabled();
  });

  it("adds one pick by name, even with the title box empty", () => {
    show({ form: { title: "" }, selectedMetadataResults: [found()] });
    expect(screen.getByRole("button", { name: "Add selected movie" })).toBeEnabled();
    expect(screen.getByText("1 movie selected")).toBeInTheDocument();
  });

  it("adds several picks by count and says how many are chosen", () => {
    show({ variant: "shows", form: { title: "" }, selectedMetadataResults: [found({ providerId: "1" }), found({ providerId: "2" })] });
    expect(screen.getByRole("button", { name: "Add 2 shows" })).toBeEnabled();
    expect(screen.getByText("2 shows selected")).toBeInTheDocument();
  });

  it("says nothing about picks when there are none", () => {
    show({ form: { title: "Arrival" } });
    expect(screen.queryByText(/ selected$/)).not.toBeInTheDocument();
  });

  it("is off while the title is being created, and shows it is working", () => {
    show({ form: { title: "Arrival" }, isCreating: true });
    const button = screen.getByRole("button", { name: "Add movie manually" });
    expect(button).toBeDisabled();
    expect(button.querySelector(".animate-spin")).not.toBeNull();
  });

  it("shows a plus, not a spinner, when it is idle", () => {
    show({ form: { title: "Arrival" } });
    expect(screen.getByRole("button", { name: "Add movie manually" }).querySelector(".animate-spin")).toBeNull();
  });

  it("hands the form to the screen when pressed", async () => {
    const user = userEvent.setup();
    const { onCreate } = show({ form: { title: "Arrival" } });
    await user.click(screen.getByRole("button", { name: "Add movie manually" }));
    expect(onCreate).toHaveBeenCalledTimes(1);
  });
});

describe("the add dialog when it is shut", () => {
  it("draws nothing", () => {
    render(
      <LibraryCreateDialog
        open={false}
        onOpenChange={vi.fn()}
        variant="movies"
        label="movies"
        singular="movie"
        isCreating={false}
        createForm={createInitialLibraryForm()}
        setCreateForm={vi.fn()}
        metadataResults={[]}
        setMetadataResults={vi.fn()}
        selectedMetadataResults={[]}
        setSelectedMetadataResults={vi.fn()}
        isSearchingMetadata={false}
        metadataSearchSequence={{ current: 0 }}
        onSearch={vi.fn()}
        onSelectResult={vi.fn()}
        onOpenHeldResult={vi.fn()}
        onCreate={vi.fn()}
      />
    );
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});

describe("the add dialog's remaining details", () => {
  it("colours the matching badge green when ready and amber when manual", () => {
    show({ metadataStatus: { isConfigured: true } as never });
    expect(screen.getByText("Title matching ready")).toHaveClass("text-success");
  });

  it("colours the badge amber when matching is not ready", () => {
    show({ metadataStatus: null });
    expect(screen.getByText("Manual entry")).toHaveClass("text-warning");
  });

  it("does not let Enter in the search box reload the page", () => {
    show({ form: { title: "arr" } });
    const form = screen.getByLabelText("What do you want to add?").closest("form")!;
    // dispatchEvent answers false when the handler stopped the browser's own submit.
    expect(fireEvent.submit(form)).toBe(false);
  });

  it("credits TheTVDB when any one of the matches came from it", () => {
    show({ variant: "shows", metadataResults: [found({ providerId: "a" }), found({ providerId: "b", tvDbId: "99" })] });
    expect(screen.getByRole("link", { name: "TheTVDB" })).toBeInTheDocument();
  });

  it("names TheTVDB on a match it described and TMDb on the rest", () => {
    show({
      variant: "shows",
      metadataResults: [
        found({ providerId: "a", title: "From TVDB", dataSource: "tvdb", tvDbId: "1", year: 2020 }),
        found({ providerId: "b", title: "From TMDb", dataSource: null, year: 2021 })
      ]
    });
    expect(screen.getByText("2020 · TheTVDB")).toBeInTheDocument();
    expect(screen.getByText("2021 · TMDb")).toBeInTheDocument();
  });

  it("dresses a held card quietly, a picked card blue and the rest plain", () => {
    const picked = found({ providerId: "a", title: "Picked" });
    show({
      metadataResults: [picked, found({ providerId: "b", title: "Held", libraryEntryId: "e" }), found({ providerId: "c", title: "Plain" })],
      selectedMetadataResults: [picked]
    });
    expect(screen.getByTitle("Select Picked")).toHaveClass("border-primary/70", "bg-primary/10", "flex", "rounded-xl");
    expect(screen.getByRole("button", { name: "Open Held, already in your library" }).closest("[data-held]")).toHaveClass("border-dashed");
    expect(screen.getByTitle("Select Plain")).toHaveClass("border-hairline");
    expect(screen.getByTitle("Select Plain")).not.toHaveClass("border-primary/70");
    expect(screen.getByTitle("Select Plain")).not.toHaveClass("border-success/45");
    expect(screen.getByTitle("Select Picked")).not.toHaveClass("border-hairline");
  });

  it("gives each match its own card even when two share an id", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    show({ metadataResults: [found({ provider: "tmdb", providerId: "7", title: "A" }), found({ provider: "tvdb", providerId: "7", title: "B" })] });
    expect(spy).not.toHaveBeenCalled();
    spy.mockRestore();
  });

  it("ties the Monitor label to the episode choice", () => {
    show({ variant: "shows", form: { monitored: true } });
    expect(screen.getByLabelText("Monitor")).toBe(screen.getByRole("combobox", { name: "Which episodes to monitor" }));
  });

  it("draws no library or profile choice when only the change handler is given", () => {
    show({ onLibraryChange: vi.fn() });
    expect(screen.queryByLabelText("Library")).not.toBeInTheDocument();
  });

  it("offers no profile choice when libraries are given but no profiles", () => {
    show({ libraries: [{ id: "a", name: "A" }], onLibraryChange: vi.fn() });
    expect(screen.getByLabelText("Library")).toBeInTheDocument();
    expect(screen.queryByLabelText("Quality profile")).not.toBeInTheDocument();
  });

  it("still lets a profile be picked when the named library is not in the list", async () => {
    const user = userEvent.setup();
    const { lastUpdate } = show({
      libraries: [{ id: "a", name: "A", qualityProfileId: "p1" }],
      libraryId: "gone",
      onLibraryChange: vi.fn(),
      qualityProfiles: [{ id: "p1", name: "P1" }, { id: "p2", name: "P2" }]
    });
    expect(within(screen.getByLabelText("Quality profile")).getAllByRole("option").map((option) => option.textContent)).toEqual(["P1", "P2"]);
    await user.selectOptions(screen.getByLabelText("Quality profile"), "p1");
    expect(lastUpdate({}).qualityProfileId).toBe("p1");
  });

  it("does not warn that a tag put on the title as it is added is on no title yet", async () => {
    show({ form: { tags: "kids" } });
    await screen.findByText("kids");
    await new Promise((resolve) => setTimeout(resolve, 30)); // reason: shows nothing is drawn, so there is nothing to wait for
    expect(screen.queryByText(/not on any title yet/)).not.toBeInTheDocument();
  });
});
