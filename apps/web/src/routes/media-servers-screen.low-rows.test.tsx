import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../lib/use-auth";

// #1329: the media servers list at its edges and the Leaving soon section of a server's drawer.

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
vi.mock("../lib/connection-versions", () => ({ useConnectionVersions: () => ({ versions: new Map() }), versionKey: (kind: string, id: string) => `${kind}:${id}` }));
vi.mock("../components/app/connection-update-panel", () => ({ ConnectionUpdatePanel: () => null }));
vi.mock("../components/app/connection-version", () => ({ ConnectionVersionBeside: () => null }));
vi.mock("../components/app/seerr-servers-card", () => ({ SeerrServersCard: () => null }));
const loaderData = { servers: [] as unknown[], collections: [] as unknown[], seerrServers: [], seerrCleanups: [], libraries: [], qualityProfiles: [] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { MediaServersPage } = await import("./media-servers-screen");
const server = (over: Record<string, unknown> = {}) => ({
  id: "s1", kind: "plex", name: "Plex on lounge", baseUrl: "http://lounge.example.test:32400", hasToken: true, isEnabled: true, healthStatus: "healthy",
  lastHealthMessage: "Reached it.", lastHealthLatencyMs: 14, lastHealthTestUtc: new Date(Date.now() - 5 * 60_000).toISOString(), serverName: "Lounge", serverId: "abc", leavingSoonOnHome: false, ...over
});
const collection = (over: Record<string, unknown> = {}) => ({
  serverId: "s1", mediaType: "movies", members: 3, skipped: 0, syncedUtc: new Date(Date.now() - 2 * 60_000).toISOString(), attemptedUtc: new Date(Date.now() - 2 * 60_000).toISOString(), problem: null, ...over
});
const renderPage = () => render(<MemoryRouter><MediaServersPage /></MemoryRouter>);

beforeEach(() => {
  cleanup();
  loaderData.servers = [];
  loaderData.collections = [];
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => new Response(String(input).endsWith("/path-mappings") ? "[]" : "{}", { status: 200 }));
});

describe("the media servers list, low rows (#1329)", () => {
  it("says 0 servers with no server and No media servers, then counts the working ones with the columns of the list", () => {
    renderPage();
    expect(screen.getByText("0 servers")).toBeTruthy();
    expect(screen.getByText("No media servers")).toBeTruthy();
    cleanup();

    loaderData.servers = [server(), server({ id: "s2", name: "Plex on den", healthStatus: "failed" })];
    renderPage();
    expect(screen.getByText("1 of 2 working")).toBeTruthy();
    for (const column of ["Server", "Address", "Reports itself as", "Last test", "Status", "On"]) {
      expect(screen.getByRole("columnheader", { name: new RegExp(`^${column}`) }), column).toBeTruthy();
    }
    expect(screen.getByText("Plex on lounge")).toBeTruthy();
    expect(screen.getAllByText("http://lounge.example.test:32400").length).toBeGreaterThan(0);
  });

  it("the Last test cell shows the age of the test and, when the collection could not be synced, the Leaving soon problem instead of the test message", () => {
    loaderData.servers = [server(), server({ id: "s2", name: "Plex on den" })];
    loaderData.collections = [collection({ serverId: "s2", problem: "Plex refused the collection." })];
    renderPage();

    const cell = (name: string) => within(screen.getAllByRole("row").find((row) => row.textContent?.includes(name))!).getAllByRole("cell")[3].textContent ?? "";
    expect(cell("Plex on lounge")).toMatch(/5 min.*ago|5m/);
    expect(cell("Plex on lounge")).toContain("Reached it.");
    expect(cell("Plex on den")).toContain("Leaving soon collection: Plex refused the collection.");
    expect(cell("Plex on den")).not.toContain("Reached it.");
  });

  it("a server's drawer lists the collections a retention rule made, with their titles and when they were updated, or the problem, and has the Show on home switch", () => {
    loaderData.servers = [server()];
    loaderData.collections = [
      collection({ mediaType: "movies", members: 3 }),
      collection({ mediaType: "tv", members: 0, problem: "Plex refused the collection." })
    ];
    renderPage();

    fireEvent.click(screen.getByText("Plex on lounge"));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("Leaving soon")).toBeTruthy();
    expect(within(dialog).getByText("Movie collection")).toBeTruthy();
    expect(within(dialog).getByText(/3 titles · updated /)).toBeTruthy();
    expect(within(dialog).getByText("TV collection")).toBeTruthy();
    expect(within(dialog).getByText(/Plex refused the collection\. Tried .*; tried again at the next check\./)).toBeTruthy();
    expect(within(dialog).getByRole("switch", { name: /Show on home/ })).toBeTruthy();
  });

  it("a server with no collection says nothing is leaving and tells what makes one appear", () => {
    loaderData.servers = [server()];
    renderPage();

    fireEvent.click(screen.getByText("Plex on lounge"));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("nothing leaving")).toBeTruthy();
    expect(within(dialog).getByText(/When a retention rule reading this server marks a title, it appears in a Leaving soon collection here\./)).toBeTruthy();
  });
});
