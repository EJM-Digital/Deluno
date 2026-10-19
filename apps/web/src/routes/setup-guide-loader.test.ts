import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));

const { setupGuideLoader } = await import("./setup-guide-page");

const URLS = [
  "/api/settings", "/api/libraries", "/api/quality-profiles", "/api/custom-formats", "/api/indexers", "/api/download-clients",
  "/api/metadata/status", "/api/setup/progress", "/api/setup/draft", "/api/v1/guides/trash/package", "/api/security/allowed-hosts",
  "/api/download-health/content-blocklist", "/api/security/local-network-auth", "/api/updates/preferences"
];

function serve(failing?: string) {
  fetchJson.mockImplementation(async (url: string) => {
    if (url === failing) throw new Error(`${url} answered 500`);
    return { from: url };
  });
}

afterEach(() => fetchJson.mockReset());

describe("the guided setup loader", () => {
  it("asks for fourteen things, all at once, before any answer has come back", async () => {
    const release: (() => void)[] = [];
    fetchJson.mockImplementation((url: string) => new Promise((resolve) => { release.push(() => resolve({ from: url })); }));

    const loading = setupGuideLoader();

    expect(fetchJson.mock.calls.map(([url]) => url).sort()).toEqual([...URLS].sort());
    release.forEach((resolve) => resolve());
    const data = await loading;
    expect(data.settings).toEqual({ from: "/api/settings" });
    expect(data.guide).toEqual({ from: "/api/v1/guides/trash/package" });
    expect(data.updatePreferences).toEqual({ from: "/api/updates/preferences" });
  });

  it.each([
    "/api/settings", "/api/libraries", "/api/quality-profiles", "/api/custom-formats", "/api/indexers", "/api/download-clients",
    "/api/setup/progress", "/api/setup/draft", "/api/v1/guides/trash/package", "/api/security/allowed-hosts",
    "/api/security/local-network-auth", "/api/updates/preferences"
  ])("when %s fails the loader fails with that error, which sends the route to the error page instead of a blank guide", async (url) => {
    serve(url);

    await expect(setupGuideLoader()).rejects.toThrow(`${url} answered 500`);
  });

  it.each(["/api/metadata/status", "/api/download-health/content-blocklist"])("when %s fails the guide still opens, with that part empty", async (url) => {
    serve(url);

    const data = await setupGuideLoader();

    expect(data.settings).toEqual({ from: "/api/settings" });
    if (url === "/api/metadata/status") expect(data.metadataStatus).toBeNull();
    else expect(data.contentBlocklist).not.toEqual({ from: url });
  });
});
