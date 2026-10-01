import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { beforeEach, describe, expect, it } from "vitest";
import { useLibraryAddress } from "./use-library-address";
import { useLibraryFilters } from "./use-library-filters";

/** The Movies and TV Shows pages' library picker reads and writes ?library=<id> and remembers the last pick per page (#1103). */
function setup(variant: "movies" | "shows", entry: string, known: string[] | null) {
  const wrapper = ({ children }: { children: ReactNode }) => <MemoryRouter initialEntries={[entry]}>{children}</MemoryRouter>;
  return renderHook(() => {
    const location = useLocation();
    const urlLibrary = new URLSearchParams(location.search).get("library");
    const filters = useLibraryFilters(variant, null, "grid", urlLibrary);
    const change = useLibraryAddress(filters.libraryId, filters.pickLibrary, known);
    return { libraryId: filters.libraryId, change, search: location.search };
  }, { wrapper });
}

describe("the library picker in the address", () => {
  beforeEach(() => localStorage.clear());

  it("opens on the library the address names", () => {
    const { result } = setup("movies", "/movies?library=hd", ["hd", "kids"]);
    expect(result.current.libraryId).toBe("hd");
    expect(result.current.search).toBe("?library=hd");
  });

  it("writes a pick to the address and remembers it for this page, All forgets it", () => {
    const { result } = setup("movies", "/movies", ["hd", "kids"]);
    expect(result.current.libraryId).toBeNull();
    act(() => result.current.change("kids"));
    expect(result.current.libraryId).toBe("kids");
    expect(result.current.search).toBe("?library=kids");
    expect(localStorage.getItem("deluno.library.movies")).toBe("kids");
    act(() => result.current.change(null));
    expect(result.current.libraryId).toBeNull();
    expect(result.current.search).toBe("");
    expect(localStorage.getItem("deluno.library.movies")).toBeNull();
  });

  it("opens on the last library picked when the address names none, and says so in the address", () => {
    localStorage.setItem("deluno.library.movies", "kids");
    const { result } = setup("movies", "/movies", ["hd", "kids"]);
    expect(result.current.libraryId).toBe("kids");
    expect(result.current.search).toBe("?library=kids");
  });

  it("remembers movies and TV separately", () => {
    localStorage.setItem("deluno.library.movies", "kids");
    const { result } = setup("shows", "/tv", ["tvl"]);
    expect(result.current.libraryId).toBeNull();
    act(() => result.current.change("tvl"));
    expect(localStorage.getItem("deluno.library.tv")).toBe("tvl");
    expect(localStorage.getItem("deluno.library.movies")).toBe("kids");
  });

  it("falls back to All libraries for an id that no longer exists, in the address and in memory", () => {
    localStorage.setItem("deluno.library.movies", "gone");
    const { result } = setup("movies", "/movies?library=gone", ["hd", "kids"]);
    expect(result.current.libraryId).toBeNull();
    expect(result.current.search).toBe("");
    expect(localStorage.getItem("deluno.library.movies")).toBeNull();
  });

  it("does not judge an id before the libraries have loaded", () => {
    const { result } = setup("movies", "/movies?library=hd", null);
    expect(result.current.libraryId).toBe("hd");
    expect(result.current.search).toBe("?library=hd");
  });

  it("still opens on All when the browser will not remember", () => {
    const original = Storage.prototype.getItem;
    Storage.prototype.getItem = () => { throw new Error("blocked"); };
    try {
      const { result } = setup("movies", "/movies", ["hd"]);
      expect(result.current.libraryId).toBeNull();
    } finally {
      Storage.prototype.getItem = original;
    }
  });
});
