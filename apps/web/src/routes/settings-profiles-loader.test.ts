import { describe, expect, it, vi } from "vitest";

/** Quality Profiles: the page reads the overview, the custom formats, the quality model and the guide together. */

const overview = { libraries: [{ id: "l" }], qualityProfiles: [{ id: "q" }], settings: { s: 1 } };
vi.mock("./settings-overview-page", () => ({ settingsOverviewLoader: vi.fn(async () => overview) }));
const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson,
  fetchTrashGuidePackage: vi.fn(async () => ({ id: "guide" }))
}));

const { settingsProfilesLoader } = await import("./settings-profiles-page");

describe("settingsProfilesLoader", () => {
  it("returns the overview with the custom formats, the quality model and the guide", async () => {
    fetchJson.mockImplementation(async (path: string) => (path === "/api/custom-formats" ? [{ id: "cf" }] : { version: "model" }));

    expect(await settingsProfilesLoader()).toEqual({ ...overview, customFormats: [{ id: "cf" }], qualityModel: { version: "model" }, guide: { id: "guide" } });
    expect(fetchJson.mock.calls.map(([path]) => path).sort()).toEqual(["/api/custom-formats", "/api/quality-model"]);
  });
});
