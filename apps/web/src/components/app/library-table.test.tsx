import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { MediaItem } from "../../lib/media-types";
import { listColumnsFor } from "../../lib/library-list-columns";
import { DEFAULT_DISPLAY_PREFERENCES } from "../../lib/display-preferences";
import { LibraryTable, renderListCell } from "./library-table";

function item(overrides: Partial<MediaItem> & { id: string; title: string }): MediaItem {
  return {
    year: 2020,
    type: "movie",
    poster: null,
    backdrop: null,
    quality: null,
    monitored: true,
    sizeGb: null,
    rating: null,
    genres: [],
    added: "12 Aug",
    overview: "",
    ...overrides
  };
}

function renderTable(overrides: Partial<React.ComponentProps<typeof LibraryTable>> = {}) {
  const onSortChange = vi.fn();
  const props: React.ComponentProps<typeof LibraryTable> = {
    items: [
      item({
        id: "1",
        title: "Alien",
        studio: "20th Century Fox",
        originalTitle: "Alien",
        collection: "Alien Collection",
        path: "/movies/Alien (1979)",
        runtimeMinutes: 117,
        releaseGroup: "GROUP",
        tags: ["scifi", "horror"],
        qualityProfile: "HD-1080p",
        digitalRelease: "2020-03-01T00:00:00Z"
      })
    ],
    selectedIds: [],
    onSelect: vi.fn(),
    onToggle: vi.fn(),
    onToggleAll: vi.fn(),
    allSelected: false,
    someSelected: false,
    sortField: "added",
    sortDirection: "desc",
    isComplete: true,
    onEndReached: vi.fn(),
    variant: "movies",
    columnOrder: [...listColumnsFor("movies")],
    onColumnOrderChange: vi.fn(),
    onSortChange,
    ...overrides
  };
  render(<LibraryTable {...props} />);
  return { onSortChange };
}

describe("LibraryTable Radarr-parity columns (#790)", () => {
  // The table body is virtualised (`useVirtualizer`), which never measures a
  // real viewport in jsdom, so these hit `renderListCell` directly rather
  // than through a full table render — the same function the table calls for
  // every visible row.
  const movie = item({
    id: "1",
    title: "Alien",
    studio: "20th Century Fox",
    originalTitle: "Alien",
    collection: "Alien Collection",
    path: "/movies/Alien (1979)",
    runtimeMinutes: 117,
    releaseGroup: "GROUP",
    tags: ["scifi", "horror"],
    qualityProfile: "HD-1080p",
    digitalRelease: "2020-03-01T00:00:00Z"
  });

  it("renders the nine new columns' data", () => {
    const cases: Array<[Parameters<typeof renderListCell>[0], string]> = [
      ["studio", "20th Century Fox"],
      ["collection", "Alien Collection"],
      ["path", "/movies/Alien (1979)"],
      ["releaseGroup", "GROUP"],
      ["tags", "scifi, horror"],
      ["qualityProfile", "HD-1080p"]
    ];

    for (const [column, expected] of cases) {
      render(<>{renderListCell(column, movie, "movies", DEFAULT_DISPLAY_PREFERENCES)}</>);
      expect(screen.getByText(expected)).toBeInTheDocument();
    }
  });

  it("renders Runtime through the shared formatter, and Release date as a short date", () => {
    render(<>{renderListCell("runtime", movie, "movies", DEFAULT_DISPLAY_PREFERENCES)}</>);
    expect(screen.getByText("1h 57m")).toBeInTheDocument();

    render(<>{renderListCell("releaseDate", movie, "movies", DEFAULT_DISPLAY_PREFERENCES)}</>);
    expect(screen.getByText(/2020/)).toBeInTheDocument();
  });

  it("skips a redundant Original title identical to Title", () => {
    render(<>{renderListCell("originalTitle", movie, "movies", DEFAULT_DISPLAY_PREFERENCES)}</>);
    expect(screen.getByText("—")).toBeInTheDocument();
  });

  it("dashes every one of the nine columns when the fact is unset, instead of printing nothing", () => {
    const bare = item({ id: "2", title: "Untitled" });
    const columns: Array<Parameters<typeof renderListCell>[0]> = [
      "studio", "originalTitle", "collection", "path", "releaseDate", "runtime", "releaseGroup", "tags", "qualityProfile"
    ];

    for (const column of columns) {
      const { unmount } = render(<>{renderListCell(column, bare, "movies", DEFAULT_DISPLAY_PREFERENCES)}</>);
      expect(screen.getByText("—")).toBeInTheDocument();
      unmount();
    }
  });
});

describe("LibraryTable list cells: quality, episodes and subtitles", () => {
  const cell = (column: Parameters<typeof renderListCell>[0], media: MediaItem, variant: "movies" | "shows" = "movies") =>
    render(<>{renderListCell(column, media, variant, DEFAULT_DISPLAY_PREFERENCES)}</>);

  it("shows the quality of the file held, and a dash for a film with no file", () => {
    const held = cell("quality", item({ id: "1", title: "Held", hasFile: true, currentQuality: "Bluray 1080p" }));
    expect(held.container.textContent).toMatch(/1080p/);
    held.unmount();

    const none = cell("quality", item({ id: "2", title: "None", hasFile: false, currentQuality: "Bluray 1080p" }));
    expect(none.container.textContent).toBe("—");
  });

  it("shows a show's held episodes as a bar, and a dash when none have aired", () => {
    const bar = cell("episodes", item({ id: "3", title: "Show", type: "show", airedEpisodeCount: 10, airedWithFileCount: 4 }), "shows");
    expect(bar.container.querySelector("[role='progressbar'], [style*='width'], [aria-label]")).not.toBeNull();
    bar.unmount();

    const none = cell("episodes", item({ id: "4", title: "Show", type: "show", airedEpisodeCount: 0 }), "shows");
    expect(none.container.textContent).toBe("—");
  });

  it("shows the subtitle languages held of those wanted, and a dash when none are wanted", () => {
    const some = cell("subtitles", item({ id: "5", title: "Film", hasFile: true, subtitleLanguagesWanted: 2, subtitleLanguagesHeld: 1, subtitleLanguagesSettled: 1 }));
    expect(screen.getByText("1 of 2")).toBeInTheDocument();
    expect(some.container.querySelector("[title]")?.getAttribute("title")).toMatch(/1 of 2/);
    some.unmount();

    const none = cell("subtitles", item({ id: "6", title: "Film", hasFile: true, subtitleLanguagesWanted: 0 }));
    expect(none.container.textContent).toBe("—");
  });
});

describe("LibraryTable click-to-sort headers (#790)", () => {
  it("starts an ascending sort on a column with no prior sort", () => {
    const { onSortChange } = renderTable({ sortField: "added", sortDirection: "desc" });

    screen.getByRole("button", { name: "Studio" }).click();

    expect(onSortChange).toHaveBeenCalledWith("studio", "asc");
  });

  it("toggles direction when clicking the already-active column", () => {
    const { onSortChange } = renderTable({ sortField: "studio", sortDirection: "asc" });

    screen.getByRole("button", { name: /Studio/ }).click();

    expect(onSortChange).toHaveBeenCalledWith("studio", "desc");
  });

  it("sorts by Title from the fixed Title header", () => {
    const { onSortChange } = renderTable({ sortField: "added", sortDirection: "desc" });

    screen.getByRole("button", { name: "Title" }).click();

    expect(onSortChange).toHaveBeenCalledWith("title", "asc");
  });

  it("leaves a column with no served sort field as plain text, not a button", () => {
    renderTable();

    expect(screen.queryByRole("button", { name: "Collection" })).not.toBeInTheDocument();
    expect(screen.getByText("Collection")).toBeInTheDocument();
  });
});
