import { beforeEach, describe, expect, it, vi } from "vitest";

const authedFetch = vi.hoisted(() => vi.fn());
const fetchJson = vi.hoisted(() => vi.fn());
const blockReleaseOnly = vi.hoisted(() => vi.fn());
vi.mock("./use-auth", () => ({ authedFetch }));
vi.mock("./api/client", () => ({ fetchJson }));
vi.mock("./block-release", () => ({ blockReleaseOnly }));

import { approveHeldRelease, dismissHeldRelease, findAnotherForHeldRelease, heldReleaseTitle, heldReleasesSource, type HeldReleaseRow } from "./held-releases";

const base: HeldReleaseRow = {
  historyId: "h 1", mediaType: "movie", id: "m1", episodeId: null, title: "Dune", year: 2021, episodeLabel: null, libraryId: "lib 1",
  releaseName: "Dune.2021.1080p", indexerName: "Nyaa", indexerId: "ix", downloadUrl: "https://x/get", quality: "WEB 1080p",
  sizeBytes: 1000, seeders: 5, reason: "Held for review.", heldUtc: "2026-10-08T00:00:00Z"
};
const episode: HeldReleaseRow = { ...base, mediaType: "series", id: "s 1", episodeId: "e 1", title: "The Wire", year: 2002, episodeLabel: "S01E04", releaseName: "The.Wire.S01E04" };

const ok = (body?: unknown) => ({ ok: true, json: async () => body }) as unknown as Response;
const refused = (body?: unknown, fails = false) => ({ ok: false, json: fails ? async () => { throw new Error("not json"); } : async () => body }) as unknown as Response;

describe("the releases held for approval", () => {
  beforeEach(() => { authedFetch.mockReset(); fetchJson.mockReset(); blockReleaseOnly.mockReset(); });

  it("reads them from the attention list, and starts with none", async () => {
    fetchJson.mockResolvedValue([base]);
    expect(await heldReleasesSource.fetch()).toEqual([base]);
    expect(fetchJson).toHaveBeenCalledWith("/api/attention/held-releases");
    expect(heldReleasesSource.fallback).toEqual([]);
    expect(heldReleasesSource.key).toEqual(["src", "attention-held-releases"]);
  });

  it("names a show without an episode by its title, and a film without a year by its title", () => {
    expect(heldReleaseTitle({ ...episode, episodeLabel: null })).toBe("The Wire");
    expect(heldReleaseTitle({ ...base, year: null })).toBe("Dune");
  });

  describe("dismissing", () => {
    it("asks the server to stop listing that release", async () => {
      authedFetch.mockResolvedValue(ok());
      await dismissHeldRelease(base);
      expect(authedFetch).toHaveBeenCalledWith("/api/attention/held-releases/h%201/dismiss", { method: "POST" });
    });

    it("says why when the server refuses, in its own words", async () => {
      authedFetch.mockResolvedValue(refused({ message: "It is already gone." }));
      await expect(dismissHeldRelease(base)).rejects.toThrow("It is already gone.");
    });

    it("uses the first validation message when there is no message", async () => {
      authedFetch.mockResolvedValue(refused({ errors: { Id: ["Bad id.", "Worse id."] } }));
      await expect(dismissHeldRelease(base)).rejects.toThrow("Bad id.");
    });

    it("says it could not be dismissed when the server gave no reason", async () => {
      authedFetch.mockResolvedValue(refused({}));
      await expect(dismissHeldRelease(base)).rejects.toThrow("That could not be dismissed.");
      authedFetch.mockResolvedValue(refused(undefined, true));
      await expect(dismissHeldRelease(base)).rejects.toThrow("That could not be dismissed.");
      authedFetch.mockResolvedValue(refused({ message: "", errors: {} }));
      await expect(dismissHeldRelease(base)).rejects.toThrow("That could not be dismissed.");
    });
  });

  describe("approving", () => {
    it("asks the server to judge the release again and send it", async () => {
      authedFetch.mockResolvedValue(ok());
      await approveHeldRelease(base);
      expect(authedFetch).toHaveBeenCalledWith("/api/attention/held-releases/h%201/approve", { method: "POST" });
    });

    it("shows the server's plain reason when it refuses", async () => {
      authedFetch.mockResolvedValue(refused({ message: "Today's rules refuse that release." }));
      await expect(approveHeldRelease(base)).rejects.toThrow("Today's rules refuse that release.");
    });

    it("names the release when the server gave no reason", async () => {
      authedFetch.mockResolvedValue(refused(null));
      await expect(approveHeldRelease(base)).rejects.toThrow("Dune.2021.1080p could not be sent to a download client.");
    });
  });

  describe("finding another", () => {
    it("refuses a film's release, searches the film again in its library, and stops listing it", async () => {
      blockReleaseOnly.mockResolvedValue({ response: ok() });
      authedFetch.mockResolvedValue(ok());

      await findAnotherForHeldRelease(base);

      expect(blockReleaseOnly).toHaveBeenCalledWith({ releaseName: "Dune.2021.1080p", indexerName: "Nyaa", mediaType: "movie", entityId: "m1", title: "Dune (2021)" });
      expect(authedFetch).toHaveBeenNthCalledWith(1, "/api/movies/m1/search?libraryId=lib%201", { method: "POST" });
      expect(authedFetch).toHaveBeenNthCalledWith(2, "/api/attention/held-releases/h%201/dismiss", { method: "POST" });
    });

    it("searches a film again without a library when it has none", async () => {
      blockReleaseOnly.mockResolvedValue({ response: ok() });
      authedFetch.mockResolvedValue(ok());
      await findAnotherForHeldRelease({ ...base, libraryId: "" });
      expect(authedFetch).toHaveBeenNthCalledWith(1, "/api/movies/m1/search", { method: "POST" });
    });

    it("searches for that one episode when the release was for an episode", async () => {
      blockReleaseOnly.mockResolvedValue({ response: ok() });
      authedFetch.mockResolvedValue(ok());

      await findAnotherForHeldRelease(episode);

      expect(blockReleaseOnly).toHaveBeenCalledWith(expect.objectContaining({ mediaType: "tv", entityId: "s 1", title: "The Wire S01E04" }));
      expect(authedFetch).toHaveBeenNthCalledWith(1, "/api/series/s%201/episodes/search", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ episodeIds: ["e 1"], monitoredOnly: false })
      });
    });

    it("searches the show again when the release has no episode", async () => {
      blockReleaseOnly.mockResolvedValue({ response: ok() });
      authedFetch.mockResolvedValue(ok());
      await findAnotherForHeldRelease({ ...episode, episodeId: null });
      expect(authedFetch).toHaveBeenNthCalledWith(1, "/api/series/s%201/search?libraryId=lib%201", { method: "POST" });
    });

    it("stops with the server's words when the release could not be refused, and searches for nothing", async () => {
      blockReleaseOnly.mockResolvedValue({ response: refused({ message: "Already refused." }) });
      await expect(findAnotherForHeldRelease(base)).rejects.toThrow("Already refused.");
      expect(authedFetch).not.toHaveBeenCalled();
    });

    it("names the release when the refusal gave no reason", async () => {
      blockReleaseOnly.mockResolvedValue({ response: refused({}) });
      await expect(findAnotherForHeldRelease(base)).rejects.toThrow("Dune.2021.1080p could not be refused.");
    });

    it("says the release was refused but the new search could not start, and leaves it listed", async () => {
      blockReleaseOnly.mockResolvedValue({ response: ok() });
      authedFetch.mockResolvedValue(refused({}));
      await expect(findAnotherForHeldRelease(base)).rejects.toThrow("The release was refused, but the new search could not be started.");
      expect(authedFetch).toHaveBeenCalledTimes(1);
    });
  });
});
