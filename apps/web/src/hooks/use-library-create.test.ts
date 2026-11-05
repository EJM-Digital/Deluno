import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MetadataSearchResult } from "../lib/api";
import {
  createInitialLibraryForm,
  metadataCreatePayload,
  parseIdPrefixedSearch,
  readRememberedAddTags,
  rememberAddTags,
  SHOW_MONITOR_PRESETS,
  setAsideAddDraft,
  useLibraryCreate
} from "./use-library-create";

describe("parseIdPrefixedSearch (#792)", () => {
  it("recognizes a tmdb: prefix", () => {
    expect(parseIdPrefixedSearch("tmdb:157336")).toEqual({ source: "tmdb", id: "157336" });
  });

  it("recognizes an imdb: prefix", () => {
    expect(parseIdPrefixedSearch("imdb:tt0816692")).toEqual({ source: "imdb", id: "tt0816692" });
  });

  it("recognizes a tvdb: prefix", () => {
    expect(parseIdPrefixedSearch("tvdb:78804")).toEqual({ source: "tvdb", id: "78804" });
  });

  it("is case-insensitive on the prefix and trims surrounding whitespace", () => {
    expect(parseIdPrefixedSearch("  IMDB:tt0816692  ")).toEqual({ source: "imdb", id: "tt0816692" });
  });

  it("does not match an ordinary title, even one containing a colon", () => {
    expect(parseIdPrefixedSearch("Severance: Season One")).toBeNull();
  });

  it("does not match a bare id with no prefix", () => {
    expect(parseIdPrefixedSearch("157336")).toBeNull();
  });

  it("does not match a prefix with nothing after the colon", () => {
    expect(parseIdPrefixedSearch("tmdb:")).toBeNull();
  });
});

const metadata: MetadataSearchResult = {
  provider: "tmdb",
  providerId: "157336",
  mediaType: "movie",
  title: "Interstellar",
  originalTitle: "Interstellar (original)",
  year: 2014,
  overview: "Explorers travel through a wormhole.",
  posterUrl: "https://img.example/poster.jpg",
  backdropUrl: "https://img.example/backdrop.jpg",
  rating: 8.6,
  genres: ["Adventure", "Drama", "Science Fiction"],
  imdbId: "tt0816692",
  externalUrl: "https://www.themoviedb.org/movie/157336",
  libraryEntryId: "entry-1"
};

describe("a new add-title form", () => {
  it("starts empty, monitored, searching straight away, and with every episode wanted", () => {
    expect(createInitialLibraryForm()).toEqual({
      title: "",
      year: "",
      imdbId: "",
      monitored: true,
      monitorPreset: "all",
      searchNow: true,
      qualityProfileId: "",
      tags: "",
      metadata: null
    });
  });

  it("offers the seven monitoring choices in order, each with a sentence of help", () => {
    expect(SHOW_MONITOR_PRESETS.map((preset) => preset.value)).toEqual(["all", "future", "missing", "firstSeason", "recent", "pilot", "none"]);
    expect(SHOW_MONITOR_PRESETS.every((preset) => preset.label && preset.help)).toBe(true);
  });
});

describe("remembered tags", () => {
  beforeEach(() => window.localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it("comes back for the same library and not for another", () => {
    rememberAddTags("lib-1", "4k, keep");
    expect(readRememberedAddTags("lib-1")).toBe("4k, keep");
    expect(readRememberedAddTags("lib-2")).toBe("");
    expect(window.localStorage.getItem("deluno:add-tags:lib-1")).toBe("4k, keep");
  });

  it("forgets the tags when the box is emptied or holds only spaces", () => {
    rememberAddTags("lib-1", "4k");
    rememberAddTags("lib-1", "   ");
    expect(readRememberedAddTags("lib-1")).toBe("");
    expect(window.localStorage.getItem("deluno:add-tags:lib-1")).toBeNull();
  });

  it("does nothing without a library", () => {
    rememberAddTags(null, "4k");
    rememberAddTags(undefined, "4k");
    rememberAddTags("", "4k");
    expect(window.localStorage.length).toBe(0);
    expect(readRememberedAddTags(null)).toBe("");
    expect(readRememberedAddTags(undefined)).toBe("");
    window.localStorage.setItem("deluno:add-tags:", "stray");
    window.localStorage.setItem("deluno:add-tags:null", "stray");
    window.localStorage.setItem("deluno:add-tags:undefined", "stray");
    expect(readRememberedAddTags("")).toBe("");
    expect(readRememberedAddTags(null)).toBe("");
    expect(readRememberedAddTags(undefined)).toBe("");
  });

  it("carries on when the browser will not let it store or read", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => { throw new Error("blocked"); });
    expect(readRememberedAddTags("lib-1")).toBe("");
    expect(() => rememberAddTags("lib-1", "4k")).not.toThrow();
    expect(() => rememberAddTags("lib-1", "")).not.toThrow();
  });
});

describe("what a title sends when it is added", () => {
  it("sends nothing extra when no search result was picked", () => {
    expect(metadataCreatePayload(null)).toEqual({});
  });

  it("sends the picked result's details, the genres on one line, and the result without the 'already have it' marker", () => {
    const payload = metadataCreatePayload(metadata);
    const { libraryEntryId: _marker, ...stored } = metadata;
    expect(payload).toEqual({
      metadataProvider: "tmdb",
      metadataProviderId: "157336",
      originalTitle: "Interstellar (original)",
      overview: "Explorers travel through a wormhole.",
      posterUrl: "https://img.example/poster.jpg",
      backdropUrl: "https://img.example/backdrop.jpg",
      rating: 8.6,
      genres: "Adventure, Drama, Science Fiction",
      externalUrl: "https://www.themoviedb.org/movie/157336",
      metadataJson: JSON.stringify(stored)
    });
    expect(payload.metadataJson).not.toContain("libraryEntryId");
  });
});

describe("the add-title state of a library screen", () => {
  it("starts closed, not creating, with an empty form and no results", () => {
    const { result } = renderHook(() => useLibraryCreate("movies", false));
    expect(result.current.showCreate).toBe(false);
    expect(result.current.isCreating).toBe(false);
    expect(result.current.isSearchingMetadata).toBe(false);
    expect(result.current.metadataResults).toEqual([]);
    expect(result.current.selectedMetadataResults).toEqual([]);
    expect(result.current.createForm).toEqual(createInitialLibraryForm());
    expect(result.current.metadataSearchSequence.current).toBe(0);
  });

  it("starts open when the screen was opened to add something", () => {
    const { result } = renderHook(() => useLibraryCreate("movies", true));
    expect(result.current.showCreate).toBe(true);
  });

  it("opens when an add is asked for later, and stays closed when the request is withdrawn", () => {
    const { result, rerender } = renderHook(({ requested }) => useLibraryCreate("movies", requested), { initialProps: { requested: false } });
    rerender({ requested: true });
    expect(result.current.showCreate).toBe(true);
    act(() => result.current.setShowCreate(false));
    rerender({ requested: false });
    expect(result.current.showCreate).toBe(false);
  });

  it("clears a half-filled form when the screen switches between movies and shows", () => {
    const { result, rerender } = renderHook(({ variant }: { variant: "movies" | "shows" }) => useLibraryCreate(variant, false), {
      initialProps: { variant: "movies" as "movies" | "shows" }
    });
    act(() => result.current.setCreateForm({ ...createInitialLibraryForm(), title: "Half typed" }));
    expect(result.current.createForm.title).toBe("Half typed");
    rerender({ variant: "shows" });
    expect(result.current.createForm.title).toBe("");
  });

  it("keeps what is typed while the screen stays the same", () => {
    const { result, rerender } = renderHook(() => useLibraryCreate("movies", false));
    act(() => result.current.setCreateForm({ ...createInitialLibraryForm(), title: "Typed" }));
    rerender();
    expect(result.current.createForm.title).toBe("Typed");
  });

  it("holds the creating, searching and results state the dialog sets", () => {
    const { result } = renderHook(() => useLibraryCreate("shows", false));
    act(() => {
      result.current.setIsCreating(true);
      result.current.setIsSearchingMetadata(true);
      result.current.setMetadataResults([metadata]);
      result.current.setSelectedMetadataResults([metadata]);
    });
    expect(result.current.isCreating).toBe(true);
    expect(result.current.isSearchingMetadata).toBe(true);
    expect(result.current.metadataResults).toEqual([metadata]);
    expect(result.current.selectedMetadataResults).toEqual([metadata]);
  });
});

describe("parseIdPrefixedSearch edge cases", () => {
  it("lets spaces follow the colon, and keeps the id as typed", () => {
    expect(parseIdPrefixedSearch("tvdb:   78804")).toEqual({ source: "tvdb", id: "78804" });
    expect(parseIdPrefixedSearch("TMDB:AbC")).toEqual({ source: "tmdb", id: "AbC" });
  });

  it("does not match an id with spaces inside it, or a prefix that is not at the start", () => {
    expect(parseIdPrefixedSearch("tmdb:12 34")).toBeNull();
    expect(parseIdPrefixedSearch("see tmdb:1234")).toBeNull();
    expect(parseIdPrefixedSearch("tmdbx:1234")).toBeNull();
  });
});

describe.each(["movies", "shows"] as const)("the add screen set aside for a held %s title (#1507)", (variant) => {
  beforeEach(() => window.sessionStorage.clear());
  const held = { provider: "tmdb", providerId: "1", mediaType: variant === "movies" ? "movies" : "tv", title: "Held", libraryEntryId: "e1", genres: [], ratings: [], cast: [], crew: [] } as unknown as MetadataSearchResult;
  const free = { ...held, providerId: "2", title: "Free", libraryEntryId: null } as MetadataSearchResult;

  it("comes back with the search text, the matches and the selections", () => {
    setAsideAddDraft(variant, { createForm: { ...createInitialLibraryForm(), title: "held" }, metadataResults: [held, free], selectedMetadataResults: [free] });
    const { result } = renderHook(() => useLibraryCreate(variant, true));
    expect(result.current.showCreate).toBe(true);
    expect(result.current.createForm.title).toBe("held");
    expect(result.current.metadataResults).toEqual([held, free]);
    expect(result.current.selectedMetadataResults).toEqual([free]);
    expect(result.current.restoredSearchTitle.current).toBe("held");
  });

  it("is not brought back once it is old", () => {
    setAsideAddDraft(variant, { createForm: { ...createInitialLibraryForm(), title: "held" }, metadataResults: [held], selectedMetadataResults: [] });
    vi.spyOn(Date, "now").mockReturnValue(Date.now() + 2 * 60 * 60 * 1000);
    const { result } = renderHook(() => useLibraryCreate(variant, true));
    expect(result.current.createForm.title).toBe("");
    expect(result.current.metadataResults).toEqual([]);
  });

  it("is ignored when the add screen was not asked for", () => {
    setAsideAddDraft(variant, { createForm: { ...createInitialLibraryForm(), title: "held" }, metadataResults: [held], selectedMetadataResults: [] });
    const { result } = renderHook(() => useLibraryCreate(variant, false));
    expect(result.current.createForm.title).toBe("");
  });
});
