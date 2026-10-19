import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { CatalogueFacets } from "../../lib/api";
import type { LibraryControlSet } from "../../lib/library-controls";
import { ControlRail, quickFiltersFor, type LibraryControls } from "./library-control-rail";

const mocks = vi.hoisted(() => ({ fetchJson: vi.fn(), providers: { sources: [] as Array<{ source: string; isConfigured: boolean }> } }));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.reject(new Error("not used"))) }));

/**
 * The toolbar above a shelf (coverage register, area library): the library picker, the sort menu and its direction button, the
 * layout choices in the View drawer, "What each poster shows", the TV pills and the Filter button. Every control reports the choice
 * it was given, from the server's own declaration, and says in words what it is for.
 */
const CONTROL_SET: LibraryControlSet = {
  kind: "movies",
  filterFields: [{ id: "year", label: "Year", hint: "When it came out.", group: "title", valueKind: "year", operators: ["min", "max"], options: null }],
  sortFields: [
    { id: "title", label: "Title", hint: "A to Z." },
    { id: "added", label: "Date added", hint: "Newest first by default." },
    { id: "year", label: "Year", hint: "Release year." },
    { id: "rating", label: "Rating", hint: "Best rated first." },
    { id: "size", label: "File size", hint: "Biggest first." },
    { id: "quality", label: "Quality", hint: "Highest tier first." },
    { id: "bitrate", label: "Bitrate", hint: "Approximate, from size and runtime." }
  ],
  posterOptions: [
    { id: "showQualityBadge", label: "Quality badge", description: "The tier of the file", defaultOn: true, line: false },
    { id: "showRating", label: "Rating", description: "The TMDb score", defaultOn: false, line: false },
    { id: "showImdb", label: "IMDb score", description: "The IMDb score", defaultOn: false, line: true, requires: "omdb" }
  ]
};

function facets(overrides: Partial<CatalogueFacets> = {}): CatalogueFacets {
  return { all: 0, monitored: 0, unmonitored: 0, downloaded: 0, missing: 0, upgrades: 0, covered: 0, upcoming: 0, downloading: 0, airing: 0, ...overrides };
}

function controls(overrides: Partial<LibraryControls> = {}): LibraryControls {
  return {
    query: "", setQuery: vi.fn(), libraryId: null, setLibraryId: vi.fn(), libraries: [], quickFilter: "all", setQuickFilter: vi.fn(),
    monitoring: "any", setMonitoring: vi.fn(), sortField: "added", setSortField: vi.fn(), sortDirection: "desc", setSortDirection: vi.fn(),
    view: "grid", setView: vi.fn(), cardSize: "md", changeSize: vi.fn(), displayOptions: {}, setDisplayOptions: vi.fn(),
    listColumnOrder: ["quality", "status", "size"] as never, setListColumnOrder: vi.fn(), resetListColumnOrder: vi.fn(),
    controlSet: CONTROL_SET, conditions: [], setConditions: vi.fn(), clearConditions: vi.fn(), savedPresets: [], newPresetName: "",
    setNewPresetName: vi.fn(), isSavingPreset: false, saveCurrentPreset: vi.fn(), applyPreset: vi.fn(), deletePreset: vi.fn(),
    updatePresetAutomation: vi.fn(), activeFilterCount: 0, ...overrides
  };
}

beforeEach(() => {
  mocks.providers.sources = [];
  mocks.fetchJson.mockReset();
  mocks.fetchJson.mockImplementation(async (url: string) => (url === "/api/metadata/status" ? { sources: mocks.providers.sources } : url === "/api/quality-model" ? { tiers: [] } : []));
});

describe("the library picker", () => {
  const libraries = [
    { id: "lib-a", name: "4K films", mediaType: "movies" },
    { id: "lib-b", name: "Kids films", mediaType: "movies" }
  ] as never;

  it("lists All libraries and then each library in the order given, shows the chosen one, and reports the choice", () => {
    const setLibraryId = vi.fn();
    render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ libraries, libraryId: "lib-b", setLibraryId })} />);

    const picker = screen.getByRole("combobox", { name: "Library" });
    expect(picker).toHaveTextContent("Kids films");
    fireEvent.click(picker);
    expect(within(screen.getByRole("listbox", { name: "Library" })).getAllByRole("option").map((option) => option.textContent)).toEqual(["All libraries", "4K films", "Kids films"]);
    fireEvent.click(screen.getByRole("option", { name: "4K films" }));
    expect(setLibraryId).toHaveBeenLastCalledWith("lib-a");

    fireEvent.click(screen.getByRole("combobox", { name: "Library" }));
    fireEvent.click(screen.getByRole("option", { name: "All libraries" }));
    expect(setLibraryId).toHaveBeenLastCalledWith(null);
  });

  it("says All libraries when none is chosen", () => {
    render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ libraries })} />);

    expect(screen.getByRole("combobox", { name: "Library" })).toHaveTextContent("All libraries");
  });
});

describe("the sort menu and its direction button", () => {
  it("offers every sort the server declared with its hint, and choosing one reports it", () => {
    const setSortField = vi.fn();
    render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ setSortField })} />);

    fireEvent.click(screen.getByRole("combobox", { name: "Sort" }));
    const options = within(screen.getByRole("listbox", { name: "Sort" })).getAllByRole("option");
    expect(options.map((option) => option.textContent)).toEqual(expect.arrayContaining(["Title", "Date added", "Year", "Rating", "File size", "Quality", "Bitrate"].map((label) => expect.stringContaining(label))));
    expect(options).toHaveLength(7);
    fireEvent.click(screen.getByRole("option", { name: /^Year/ }));
    expect(setSortField).toHaveBeenLastCalledWith("year");
  });

  it("the direction button says which way the list runs and what it will switch to, and flips it", () => {
    const setSortDirection = vi.fn();
    const { rerender } = render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ sortDirection: "asc", setSortDirection })} />);

    const ascending = screen.getByRole("button", { name: "Ascending — switch to descending" });
    expect(ascending).toHaveAttribute("title", "A–Z, oldest first, lowest value");
    fireEvent.click(ascending);
    expect(setSortDirection).toHaveBeenLastCalledWith("desc");

    rerender(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ sortDirection: "desc", setSortDirection })} />);
    const descending = screen.getByRole("button", { name: "Descending — switch to ascending" });
    expect(descending).toHaveAttribute("title", "Z–A, newest first, highest value");
    fireEvent.click(descending);
    expect(setSortDirection).toHaveBeenLastCalledWith("asc");
  });
});

describe("the View drawer", () => {
  const open = () => fireEvent.click(screen.getByText("View"));

  it("offers Poster grid, Overview and Compact list, marks the chosen one, and reports each choice", () => {
    const setView = vi.fn();
    render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ view: "overview", setView })} />);
    open();

    const drawer = screen.getByRole("dialog");
    for (const [label, mode] of [["Poster grid", "grid"], ["Overview", "overview"], ["Compact list", "list"]] as const) {
      expect(within(drawer).getByText(label)).toBeInTheDocument();
      fireEvent.click(within(drawer).getByText(label).closest("button")!);
      expect(setView).toHaveBeenLastCalledWith(mode);
    }
    expect(within(within(drawer).getByText("Overview").closest("button")!).getByText("Selected")).toBeInTheDocument();
    expect(within(drawer).queryByText("Poster size")).toBeNull();
    expect(within(drawer).queryByText("List columns")).toBeNull();
  });

  it("the grid has a poster size, and the list has its column order and a Reset", () => {
    const changeSize = vi.fn();
    const resetListColumnOrder = vi.fn();
    const { rerender } = render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ view: "grid", changeSize })} />);
    open();
    fireEvent.click(screen.getByText("Large").closest("button")!);
    expect(changeSize).toHaveBeenLastCalledWith("lg");

    rerender(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ view: "list", resetListColumnOrder })} />);
    expect(screen.getByText("List columns")).toBeInTheDocument();
    expect(screen.getByLabelText("Current list column order")).toHaveTextContent(/Quality · Status · Size/i);
    fireEvent.click(screen.getByRole("button", { name: "Reset" }));
    expect(resetListColumnOrder).toHaveBeenCalledTimes(1);
  });
});

describe("What each poster shows", () => {
  const open = () => fireEvent.click(screen.getByText("View"));

  it("draws a switch per served option, on or off by its default until a choice is remembered, and a flip reports the whole choice", () => {
    const setDisplayOptions = vi.fn();
    render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ displayOptions: { showRating: true }, setDisplayOptions })} />);
    open();

    expect(screen.getByRole("switch", { name: /Quality badge/ })).toBeChecked();
    expect(screen.getByRole("switch", { name: /^Rating/ })).toBeChecked();
    expect(screen.getByRole("switch", { name: /IMDb score/ })).not.toBeChecked();
    fireEvent.click(screen.getByRole("switch", { name: /Quality badge/ }));
    expect(setDisplayOptions).toHaveBeenLastCalledWith({ showRating: true, showQualityBadge: false });
  });

  it("a switch that needs OMDb says it needs a key until a key is set", async () => {
    const { unmount } = render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls()} />);
    open();
    expect(await screen.findByText(/The IMDb score\. Needs an OMDB key — nothing to show until then\./)).toBeInTheDocument();
    unmount();

    mocks.providers.sources = [{ source: "omdb", isConfigured: true }];
    render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls()} />);
    open();
    await waitFor(() => expect(screen.queryByText(/Needs an OMDB key/)).toBeNull());
    expect(screen.getByText("The IMDb score")).toBeInTheDocument();
  });
});

describe("the TV pills", () => {
  it("offer Continuing for shows and not for films, each with its own server count, and choosing it reports airing", () => {
    expect(quickFiltersFor("shows").map((chip) => chip.key)).toContain("airing");
    expect(quickFiltersFor("movies").map((chip) => chip.key)).not.toContain("airing");
    const setQuickFilter = vi.fn();
    render(<ControlRail label="TV" variant="shows" facets={facets({ all: 40, airing: 7, covered: 20 })} controls={controls({ setQuickFilter, controlSet: { ...CONTROL_SET, kind: "shows" } })} />);

    const continuing = screen.getByRole("button", { name: /Continuing/ });
    expect(continuing).toHaveTextContent("7");
    fireEvent.click(continuing);
    expect(setQuickFilter).toHaveBeenLastCalledWith("airing");
    expect(screen.getByRole("button", { name: /^All/ })).toHaveTextContent("40");
  });
});

describe("the Filter button", () => {
  it("says none while nothing narrows the shelf and how many while something does", () => {
    const { rerender } = render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls()} />);
    expect(screen.getByText("none")).toBeInTheDocument();

    rerender(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ activeFilterCount: 2 })} />);
    expect(screen.getByText("2 narrowing")).toBeInTheDocument();
  });

  it("opens the drawer with the saved filters and the questions this shelf asks, and Add a field reports a new condition", async () => {
    const setConditions = vi.fn();
    render(<ControlRail label="Movies" variant="movies" facets={facets()} controls={controls({ setConditions })} />);

    fireEvent.click(screen.getByText("Filter"));
    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getByText("Saved filters")).toBeInTheDocument();
    expect(within(drawer).getByText(/Nothing saved yet\./)).toBeInTheDocument();
    fireEvent.click(within(drawer).getByRole("button", { name: "Add a field" }));
    fireEvent.click(within(drawer).getByRole("button", { name: /^Year/ }));

    expect(setConditions).toHaveBeenLastCalledWith([{ field: "year", operator: "min", values: [] }]);
  });
});
