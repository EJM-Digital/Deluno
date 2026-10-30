import { act, render, renderHook, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ListGroupHeader, MediaTypeFilter, mediaTypeLabel, normalizeMediaType, useMediaTypeSplit } from "./media-type-split";

describe("which side of the app a title belongs to", () => {
  it("reads anything that is not tv as a movie", () => {
    expect(normalizeMediaType("tv")).toBe("tv");
    expect(normalizeMediaType("movies")).toBe("movies");
    expect(normalizeMediaType(null)).toBe("movies");
    expect(normalizeMediaType(undefined)).toBe("movies");
    expect(mediaTypeLabel("tv")).toBe("TV shows");
    expect(mediaTypeLabel("anything")).toBe("Movies");
  });
});

describe("the All, Movies and TV filter", () => {
  it("shows the count beside each side and tells the page which one was picked", async () => {
    const onValueChange = vi.fn();
    render(<MediaTypeFilter value="all" onValueChange={onValueChange} counts={{ movies: 3, tv: 2 }} />);
    expect(screen.getByRole("radio", { name: /^All.*5/ })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /^Movies.*3/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("radio", { name: /^TV.*2/ }));
    expect(onValueChange).toHaveBeenCalledWith("tv");
  });

  it("shows the three choices without numbers when no counts are given", () => {
    render(<MediaTypeFilter value="movies" onValueChange={vi.fn()} />);
    expect(screen.getByRole("radio", { name: "All" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Movies" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "TV" })).toBeInTheDocument();
  });

  it("is left out when one side has nothing, because there is nothing to choose", () => {
    expect(render(<MediaTypeFilter value="all" onValueChange={vi.fn()} counts={{ movies: 0, tv: 4 }} />).container).toBeEmptyDOMElement();
    expect(render(<MediaTypeFilter value="all" onValueChange={vi.fn()} counts={{ movies: 4, tv: 0 }} />).container).toBeEmptyDOMElement();
  });
});

describe("a group header inside a list", () => {
  it("shows the label alone, or with its count, detail and actions", () => {
    const { unmount } = render(<ListGroupHeader label="Movies" />);
    expect(screen.getByRole("columnheader", { name: "Movies" })).toBeInTheDocument();
    unmount();

    render(<ListGroupHeader label="Season 1" count={9} detail="6 of 9 imported" actions={<button>Search season</button>} />);
    expect(screen.getByText("6 of 9 imported")).toBeVisible();
    expect(screen.getByRole("button", { name: "Search season" })).toBeVisible();
  });

  it("shows a count of zero", () => {
    render(<ListGroupHeader label="Specials" count={0} />);
    expect(screen.getByRole("row")).toHaveTextContent("Specials0");
  });
});

describe("splitting a list by media type", () => {
  const items = [
    { name: "Arrival", type: "movies" },
    { name: "Severance", type: "tv" },
    { name: "Sintel", type: null },
    { name: "Andor", type: "tv" }
  ];
  const typeOf = (item: { type: string | null }) => item.type;

  it("groups both sides when everything is shown, and counts them", () => {
    const { result } = renderHook(() => useMediaTypeSplit(items, typeOf));
    expect(result.current.counts).toEqual({ movies: 2, tv: 2 });
    expect(result.current.showGroups).toBe(true);
    expect(result.current.groups.map((group) => group.label)).toEqual(["Movies", "TV shows"]);
    expect(result.current.visibleCount).toBe(4);
  });

  it("narrows to one side when it is picked", () => {
    const { result } = renderHook(() => useMediaTypeSplit(items, typeOf));
    act(() => result.current.setScope("tv"));
    expect(result.current.groups.map((group) => group.items.map((item) => item.name))).toEqual([["Severance", "Andor"]]);
    expect(result.current.visibleCount).toBe(2);

    act(() => result.current.setScope("movies"));
    expect(result.current.groups[0].label).toBe("Movies");
    expect(result.current.visibleCount).toBe(2);
  });

  it("does not group a list that only has one side, and drops the empty group", () => {
    const { result } = renderHook(() => useMediaTypeSplit([items[0]], typeOf));
    expect(result.current.showGroups).toBe(false);
    expect(result.current.groups.map((group) => group.label)).toEqual(["Movies"]);
  });
});
