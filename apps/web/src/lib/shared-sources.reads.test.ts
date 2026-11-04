import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());
const fetchPageItemsMock = vi.hoisted(() => vi.fn());
const fetchFailedJobsMock = vi.hoisted(() => vi.fn());
vi.mock("./api", async (importOriginal) => ({ ...(await importOriginal<typeof import("./api")>()), fetchJson: fetchJsonMock, fetchPageItems: fetchPageItemsMock }));
vi.mock("./failed-jobs", () => ({ fetchFailedJobs: fetchFailedJobsMock }));

const { sharedSources, emptyRecovery } = await import("./shared-sources");

describe("the lists several screens read together", () => {
  beforeEach(() => { fetchJsonMock.mockReset(); fetchPageItemsMock.mockReset(); fetchFailedJobsMock.mockReset(); });

  it.each([
    ["telemetry", "/api/download-clients/telemetry"],
    ["movieRecovery", "/api/movies/import-recovery?limit=500"],
    ["seriesRecovery", "/api/series/import-recovery?limit=500"],
    ["libraries", "/api/libraries"],
    ["settings", "/api/settings"],
    ["indexers", "/api/indexers"],
    ["downloadClients", "/api/download-clients"],
    ["healthSummary", "/api/system/health/summary"],
    ["movieWanted", "/api/movies/wanted"],
    ["seriesWanted", "/api/series/wanted"]
  ] as const)("%s reads %s and hands the answer on unchanged", async (name, path) => {
    fetchJsonMock.mockResolvedValue({ answer: name });
    expect(await sharedSources[name].fetch()).toEqual({ answer: name });
    expect(fetchJsonMock).toHaveBeenCalledWith(path);
  });

  it("reads the newest sixty sent downloads", async () => {
    fetchPageItemsMock.mockResolvedValue([{ id: "d1" }]);
    expect(await sharedSources.dispatches.fetch()).toEqual([{ id: "d1" }]);
    expect(fetchPageItemsMock).toHaveBeenCalledWith("/api/download-dispatches?pageSize=60");
  });

  it("reads the failed jobs wherever they sit in the queue", async () => {
    fetchFailedJobsMock.mockResolvedValue([{ id: "j1" }]);
    expect(await sharedSources.failedJobs.fetch()).toEqual([{ id: "j1" }]);
  });

  it("shows no leftover download folders from an older API, whatever went wrong", async () => {
    fetchJsonMock.mockResolvedValue([{ path: "a" }]);
    expect(await sharedSources.orphans.fetch()).toEqual([{ path: "a" }]);
    expect(fetchJsonMock).toHaveBeenCalledWith("/api/download-clients/orphans");
    fetchJsonMock.mockRejectedValue(new Error("not found"));
    expect(await sharedSources.orphans.fetch()).toEqual([]);
  });

  it("names every list in words a sentence can use, under its own key", () => {
    const entries = Object.values(sharedSources);
    expect(new Set(entries.map((source) => source.key.join(":"))).size).toBe(entries.length);
    expect(sharedSources.orphans.label).toBe("Leftover download folders");
    expect(sharedSources.failedJobs.key).toEqual(["src", "failed-jobs"]);
  });

  it("starts an import recovery summary with nothing open", () => {
    expect(emptyRecovery).toEqual({ openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] });
  });
});
