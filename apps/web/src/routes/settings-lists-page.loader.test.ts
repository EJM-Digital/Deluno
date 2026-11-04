import { beforeEach, describe, expect, it, vi } from "vitest";

/** Settings > Import lists, the loader (#1329): what the page asks for, and a failure that reaches the router. */

const fetchJson = vi.hoisted(() => vi.fn());
const settingsOverviewLoader = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("./settings-overview-page", () => ({ settingsOverviewLoader }));

const { settingsListsLoader } = await import("./settings-lists-page");

beforeEach(() => {
  fetchJson.mockReset();
  settingsOverviewLoader.mockReset();
});

describe("settingsListsLoader", () => {
  it("adds the never-add decisions and the Trakt state to what the overview already reads", async () => {
    settingsOverviewLoader.mockResolvedValue({ intakeSources: [], libraries: [], settings: { offListTag: "gone" } });
    fetchJson.mockImplementation(async (url: string) => (url === "/api/exclusions" ? [{ id: "x1" }] : { clientConfigured: true, connected: false }));

    await expect(settingsListsLoader()).resolves.toEqual({
      intakeSources: [], libraries: [], settings: { offListTag: "gone" }, exclusions: [{ id: "x1" }], traktStatus: { clientConfigured: true, connected: false }
    });
    expect(fetchJson).toHaveBeenCalledWith("/api/exclusions");
    expect(fetchJson).toHaveBeenCalledWith("/api/trakt/status");
  });

  it("lets a refused read reach the router", async () => {
    settingsOverviewLoader.mockResolvedValue({});
    fetchJson.mockRejectedValue(new Error("Exclusions could not be read."));
    await expect(settingsListsLoader()).rejects.toThrow("Exclusions could not be read.");
  });
});
