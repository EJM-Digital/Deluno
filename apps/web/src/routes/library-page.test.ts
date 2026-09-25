import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * Settings -> Interface's default movie/show view used to have no effect:
 * useLibraryFilters read only localStorage, so a library this browser had
 * never opened before always started in the hardcoded grid (#736). These
 * loaders are what feeds the real saved default into LibraryView.
 */
const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson
}));

const { moviesLoader, showsLoader } = await import("./library-page");

describe("library page loaders carry the saved default view (#736)", () => {
  afterEach(() => fetchJson.mockReset());

  it("passes the saved default movie view through to the movies page", async () => {
    fetchJson.mockResolvedValueOnce(null); // metadata status
    fetchJson.mockResolvedValueOnce({ defaultMovieView: "list" });

    expect(await moviesLoader()).toEqual({ metadataStatus: null, defaultView: "list" });
  });

  it("passes the saved default show view through to the shows page", async () => {
    fetchJson.mockResolvedValueOnce(null); // metadata status
    fetchJson.mockResolvedValueOnce({ defaultShowView: "list" });

    expect(await showsLoader()).toEqual({ metadataStatus: null, defaultView: "list" });
  });

  it("falls back to grid for an unrecognised or missing saved default", async () => {
    fetchJson.mockResolvedValueOnce(null);
    fetchJson.mockResolvedValueOnce({ defaultMovieView: "coverflow" });
    expect((await moviesLoader()).defaultView).toBe("grid");

    fetchJson.mockResolvedValueOnce(null);
    fetchJson.mockResolvedValueOnce(null); // settings request itself failed
    expect((await moviesLoader()).defaultView).toBe("grid");
  });
});
