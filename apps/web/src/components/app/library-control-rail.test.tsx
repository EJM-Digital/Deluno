import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { CatalogueFacets } from "../../lib/api";
import type { LibraryControlSet } from "../../lib/library-controls";
import { ControlRail, type LibraryControls } from "./library-control-rail";

// `useConfiguredProviders` fetches on mount; nothing in these tests cares
// about provider status, so the request is stubbed out rather than left to
// hit a real network call jsdom cannot make.
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.reject(new Error("not used"))) }));

const CONTROL_SET: LibraryControlSet = {
  kind: "shows",
  filterFields: [],
  sortFields: [{ id: "added", label: "Added", hint: "Newest first by default." }],
  posterOptions: []
};

function facets(overrides: Partial<CatalogueFacets> = {}): CatalogueFacets {
  return {
    all: 0,
    monitored: 0,
    unmonitored: 0,
    downloaded: 0,
    missing: 0,
    upgrades: 0,
    covered: 0,
    upcoming: 0,
    downloading: 0,
    airing: 0,
    ...overrides
  };
}

function controls(overrides: Partial<LibraryControls> = {}): LibraryControls {
  return {
    query: "",
    setQuery: vi.fn(),
    libraryId: null,
    setLibraryId: vi.fn(),
    libraries: [],
    quickFilter: "all",
    setQuickFilter: vi.fn(),
    monitoring: "any",
    setMonitoring: vi.fn(),
    sortField: "added",
    setSortField: vi.fn(),
    sortDirection: "desc",
    setSortDirection: vi.fn(),
    view: "grid",
    setView: vi.fn(),
    cardSize: "md",
    changeSize: vi.fn(),
    displayOptions: {},
    setDisplayOptions: vi.fn(),
    listColumnOrder: [],
    setListColumnOrder: vi.fn(),
    resetListColumnOrder: vi.fn(),
    controlSet: CONTROL_SET,
    conditions: [],
    setConditions: vi.fn(),
    clearConditions: vi.fn(),
    savedPresets: [],
    newPresetName: "",
    setNewPresetName: vi.fn(),
    isSavingPreset: false,
    saveCurrentPreset: vi.fn(),
    applyPreset: vi.fn(),
    deletePreset: vi.fn(),
    updatePresetAutomation: vi.fn(),
    activeFilterCount: 0,
    ...overrides
  };
}

/**
 * #934: the chip row and the Unmonitored toggle print whatever the server's
 * facets say, with no client-side re-derivation that could drift from them.
 * The backend tests (`CataloguePagingTests`) prove the *facets themselves*
 * agree with the filtered page; this proves the *toolbar* does not introduce
 * a second disagreement on top by recomputing anything itself.
 */
describe("ControlRail facet wiring", () => {
  it("prints each chip's own facet, not a client-side guess", () => {
    render(
      <ControlRail
        label="TV"
        variant="shows"
        facets={facets({ all: 2, covered: 0, airing: 0, missing: 2 })}
        controls={controls({ quickFilter: "airing" })}
      />
    );

    // The Continuing (airing) chip must show the server's own count for that
    // status, not the size of whatever happens to be on screen.
    const continuingChip = screen.getByRole("button", { name: /Continuing/ });
    expect(continuingChip).toHaveTextContent("0");
  });

  it("prints the Unmonitored toggle's facet as given, under whichever chip is selected", () => {
    render(
      <ControlRail
        label="Movies"
        variant="movies"
        facets={facets({ all: 20, upcoming: 0, unmonitored: 0 })}
        controls={controls({ quickFilter: "upcoming" })}
      />
    );

    // Selecting Upcoming and reading zero unmonitored titles here must mean
    // exactly that: turning the toggle on shows zero, never a total carried
    // over from a different chip.
    const unmonitoredToggle = screen.getByRole("button", { name: /Unmonitored/ });
    expect(unmonitoredToggle).toHaveTextContent("0");
  });
});
