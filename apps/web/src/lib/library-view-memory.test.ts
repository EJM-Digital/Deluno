import { beforeEach, describe, expect, it } from "vitest";
import { renderHook, act } from "@testing-library/react";

import { useLibraryFilters } from "../hooks/use-library-filters";

/**
 * The View drawer says the layout is remembered. It has to actually be.
 *
 * <p>Until #310 the drawer's own subtitle — "Remembered separately for movies
 * and TV" — was true of the poster size and the poster options and false of the
 * layout, which is the one control the drawer is named after. Every reload put
 * you back in the grid. With two layouts and the default being one of them, it
 * is the sort of thing that goes unnoticed for a long time.</p>
 */
describe("the shelf remembers its layout", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("keeps the chosen layout for next time", () => {
    const first = renderHook(() => useLibraryFilters("movies", null));
    expect(first.result.current.view).toBe("grid");

    act(() => first.result.current.setView("overview"));
    expect(first.result.current.view).toBe("overview");

    // A second mount is what a reload looks like from here.
    const second = renderHook(() => useLibraryFilters("movies", null));
    expect(second.result.current.view).toBe("overview");
  });

  it("remembers movies and TV separately", () => {
    const movies = renderHook(() => useLibraryFilters("movies", null));
    act(() => movies.result.current.setView("overview"));

    // A film shelf left in Overview must not decide how the TV shelf opens:
    // they are different libraries browsed for different reasons, which is the
    // whole reason the size and the poster options are stored per kind too.
    const shows = renderHook(() => useLibraryFilters("shows", null));
    expect(shows.result.current.view).toBe("grid");
  });

  it("falls back rather than trusting a layout it does not have", () => {
    // A string some earlier version of Deluno wrote, or a hand-edited one.
    localStorage.setItem("deluno-view-movies", "coverflow");

    const { result } = renderHook(() => useLibraryFilters("movies", null));
    expect(result.current.view).toBe("grid");
  });
});
