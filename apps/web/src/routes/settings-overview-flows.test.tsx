import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * The setup overview: what its loader reads, and what makes the page read it again.
 */

const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../lib/api")>()), fetchJson: fetchJsonMock }));
const liveSpec = vi.hoisted(() => ({ current: undefined as { events?: string[]; areas?: string[] } | undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: (spec: { events?: string[]; areas?: string[] }) => { liveSpec.current = spec; } }));
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData
}));
vi.mock("../components/app/settings-shell", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/app/settings-shell")>()),
  SettingsShell: ({ children }: { children: React.ReactNode }) => <div>{children}</div>
}));

const { SettingsOverviewPage, settingsOverviewLoader } = await import("./settings-overview-page");

beforeEach(() => {
  fetchJsonMock.mockReset();
  liveSpec.current = undefined;
  Object.assign(loaderData, { libraries: [], downloadClients: [], indexers: [], intakeSources: [], policySets: [], qualityProfiles: [], settings: { ...emptyPlatformSettingsSnapshot } });
});

describe("the setup overview's loader", () => {
  it("asks for settings, libraries, quality profiles, library profiles, indexers, download clients and intake sources all at once, and returns each under its own name", async () => {
    const answers: Record<string, unknown> = {
      "/api/settings": { marker: "settings" }, "/api/libraries": [{ id: "lib" }], "/api/quality-profiles": [{ id: "qp" }], "/api/policy-sets": [{ id: "plan" }],
      "/api/indexers": [{ id: "idx" }], "/api/download-clients": [{ id: "dc" }], "/api/intake-sources": [{ id: "src" }]
    };
    const releases: Array<() => void> = [];
    fetchJsonMock.mockImplementation((path: string) => new Promise((resolve) => releases.push(() => resolve(answers[path]))));

    const pending = settingsOverviewLoader();

    // All seven are asked for before any one has answered: they run side by side, not one after another.
    expect(fetchJsonMock.mock.calls.map(([path]) => path).sort()).toEqual(Object.keys(answers).sort());
    releases.forEach((release) => release());
    const result = await pending;
    expect(result).toEqual({
      settings: { marker: "settings" }, libraries: [{ id: "lib" }], qualityProfiles: [{ id: "qp" }], policySets: [{ id: "plan" }],
      indexers: [{ id: "idx" }], downloadClients: [{ id: "dc" }], intakeSources: [{ id: "src" }]
    });
  });

  it("fails as a whole, rather than showing a half-read overview, when one of the seven cannot be read", async () => {
    fetchJsonMock.mockImplementation(async (path: string) => {
      if (path === "/api/indexers") throw new Error("Indexers could not be read.");
      return [];
    });
    await expect(settingsOverviewLoader()).rejects.toThrow("Indexers could not be read.");
  });
});

describe("the setup overview page", () => {
  it("counts the steps done from what the loader read, and points at the first required step still open", () => {
    render(<MemoryRouter><SettingsOverviewPage /></MemoryRouter>);
    expect(screen.getByText(/required steps complete/)).toBeTruthy();
    expect(screen.getAllByText("Next").length).toBe(1);
  });

  it("reads again when the settings change", () => {
    render(<MemoryRouter><SettingsOverviewPage /></MemoryRouter>);
    expect(liveSpec.current?.events).toContain("SettingsChanged");
  });

  it("reads again when a library, indexer, download client or quality profile is added or removed elsewhere (it listens to SettingsChanged only, so a step's status goes stale)", () => {
    render(<MemoryRouter><SettingsOverviewPage /></MemoryRouter>);
    expect(liveSpec.current?.events).toEqual(expect.arrayContaining(["LibraryChanged", "IndexerChanged", "QualityProfileChanged"]));
  });
});

describe("the setup overview's readiness and step badges (#1329)", () => {
  const configured = (autoSearchEnabled: boolean) => ({
    libraries: [{ mediaType: "movies", rootPath: "/media/movies", autoSearchEnabled }],
    indexers: [{ isEnabled: true, healthStatus: "healthy" }],
    downloadClients: [{ isEnabled: true, healthStatus: "healthy" }],
    policySets: [{ isEnabled: true }],
    qualityProfiles: [{}],
    settings: { ...emptyPlatformSettingsSnapshot, autoStartJobs: true, workflowVerified: true }
  });

  it("an empty install is Not ready, marks only the first required step Next, and calls the import-list step Optional", () => {
    render(<MemoryRouter><SettingsOverviewPage /></MemoryRouter>);

    expect(screen.getByText("Readiness: Not ready")).toBeTruthy();
    expect(screen.getAllByText("Next")).toHaveLength(1);
    expect(screen.queryByText("Done")).toBeNull();
    expect(screen.getAllByText("Optional").length).toBeGreaterThan(0);
  });

  it("with the basics and connections done but nothing automatic on it reads Acquisition ready", () => {
    Object.assign(loaderData, configured(false));
    render(<MemoryRouter><SettingsOverviewPage /></MemoryRouter>);

    expect(screen.getByText("Readiness: Acquisition ready")).toBeTruthy();
    expect(screen.getAllByText("Done").length).toBeGreaterThan(0);
  });

  it("with everything required done it reads Operationally ready and no step is Next", () => {
    Object.assign(loaderData, configured(true));
    render(<MemoryRouter><SettingsOverviewPage /></MemoryRouter>);

    expect(screen.getByText("Readiness: Operationally ready")).toBeTruthy();
    expect(screen.queryByText("Next")).toBeNull();
    expect(screen.queryByText("Readiness: Not ready")).toBeNull();
  });
});
