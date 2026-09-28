import { fireEvent, render, screen, within } from "@testing-library/react";
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

/**
 * #959: on a phone the Library/Filter/Sort/View cluster has nowhere to wrap
 * to (the search box above already claims the width), so it used to sit at
 * its full content width with `overflow-x: visible`, cutting Sort off and
 * putting View off-screen entirely. jsdom does not lay out real pixels, so
 * this cannot assert the 390px measurement from the issue — it asserts the
 * structural fix instead: the cluster is a `flex-nowrap` row that scrolls
 * sideways (`overflow-x-auto`) rather than one that wraps or overflows.
 */
describe("ControlRail narrow-width control cluster", () => {
  it("scrolls the Library/Filter/Sort/View cluster sideways instead of wrapping or overflowing", () => {
    const { container } = render(
      <ControlRail label="Movies" variant="movies" facets={facets()} controls={controls()} />
    );

    const cluster = container.querySelector('div[class*="sm:shrink-0"][class*="sm:flex-wrap"]');
    expect(cluster).not.toBeNull();
    expect(cluster).toHaveClass("flex-nowrap");
    expect(cluster).toHaveClass("overflow-x-auto");
    expect(cluster).toHaveClass("w-full");

    // Every control the cluster holds is still present and reachable in the
    // DOM — scrolling hides none of them, it just changes how overflow is
    // handled. Library and Sort are `MenuSelect` triggers (role="combobox"),
    // named by `aria-label` rather than visible text; Filter and View are
    // the plain buttons beside them.
    const scoped = within(cluster as HTMLElement);
    expect(scoped.getByRole("combobox", { name: "Library" })).toBeInTheDocument();
    expect(scoped.getByRole("combobox", { name: "Sort" })).toBeInTheDocument();
    expect(scoped.getByText("Filter")).toBeInTheDocument();
    expect(scoped.getByText("View")).toBeInTheDocument();
  });
});

/**
 * #1032: the separate "Wanted" shortcut (#790) and the plain Missing chip were
 * the same question asked twice, so they are now one "Missing" chip meaning
 * monitored titles with no acceptable file — what Wanted meant. `missing` and
 * `monitored` stay two separate, stackable axes (see the Unmonitored toggle
 * above); this chip just sets both at once.
 */
describe("ControlRail Missing chip (#1032, merges the old Wanted shortcut)", () => {
  it("sets the Missing chip and the Monitored filter together", () => {
    const setQuickFilter = vi.fn();
    const setMonitoring = vi.fn();
    render(
      <ControlRail
        label="Movies"
        variant="movies"
        facets={facets()}
        controls={controls({ setQuickFilter, setMonitoring })}
      />
    );

    screen.getByRole("button", { name: /^Missing/ }).click();

    expect(setQuickFilter).toHaveBeenCalledWith("missing");
    expect(setMonitoring).toHaveBeenCalledWith("monitored");
  });

  it("reads as pressed only when both axes already match", () => {
    render(
      <ControlRail
        label="Movies"
        variant="movies"
        facets={facets()}
        controls={controls({ quickFilter: "missing", monitoring: "monitored" })}
      />
    );

    expect(screen.getByRole("button", { name: /^Missing/ })).toHaveAttribute("aria-pressed", "true");
  });

  it("no longer offers a separate Wanted control", () => {
    render(
      <ControlRail label="Movies" variant="movies" facets={facets()} controls={controls()} />
    );

    expect(screen.queryByRole("button", { name: /^Wanted/ })).toBeNull();
  });

  /**
   * #1032 follow-up: leaving Missing for a different status chip used to leave
   * the Monitored filter switched on behind the scenes, so the next chip's
   * printed count (the server's facet for "any" monitoring) no longer matched
   * the rows it actually showed (which were still filtered to monitored-only)
   * — the exact #934 "matches its own count" failure a person would hit by
   * clicking Missing and then, say, Upcoming. `monitoringForcedByMissing`
   * (declared beside `openPanel` above) tracks whether Missing is what set
   * `monitored`, and only a different status chip clears it back to "any";
   * a choice the person made directly on the monitoring axis is a separate
   * thing and is asserted to survive that same click in the test below it.
   */
  it("resets the Monitored filter Missing turned on when a different chip is picked next", () => {
    const setQuickFilter = vi.fn();
    const setMonitoring = vi.fn();
    render(
      <ControlRail
        label="Movies"
        variant="movies"
        facets={facets()}
        controls={controls({ setQuickFilter, setMonitoring })}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: /^Missing/ }));
    expect(setMonitoring).toHaveBeenLastCalledWith("monitored");

    fireEvent.click(screen.getByRole("button", { name: /^Upgradable/ }));

    expect(setQuickFilter).toHaveBeenLastCalledWith("upgrades");
    expect(setMonitoring).toHaveBeenLastCalledWith("any");
  });

  it("leaves an explicit Unmonitored choice made after Missing alone when a different chip is picked next", () => {
    const setQuickFilter = vi.fn();
    const setMonitoring = vi.fn();
    render(
      <ControlRail
        label="Movies"
        variant="movies"
        facets={facets()}
        controls={controls({ setQuickFilter, setMonitoring })}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: /^Missing/ }));
    // A deliberate choice on the monitoring axis itself, made through the
    // toggle rather than inherited from Missing — this is the kind of choice
    // that is supposed to stick.
    fireEvent.click(screen.getByRole("button", { name: /Unmonitored/ }));
    setMonitoring.mockClear();

    fireEvent.click(screen.getByRole("button", { name: /^Upgradable/ }));

    expect(setQuickFilter).toHaveBeenLastCalledWith("upgrades");
    expect(setMonitoring).not.toHaveBeenCalled();
  });
});
