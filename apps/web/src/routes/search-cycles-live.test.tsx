import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConfigAreas } from "../lib/config-areas";
import { emptyContentBlocklistSettings, emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * The Automation screen follows the server and nothing asks on a timer (#1179): a library, a setting, a quality profile, the
 * download-health rules or the quality model changing anywhere reads the screen again, and a library's automation moving or a
 * search cycle starting, advancing or finishing reads only the schedule lists again.
 */

const mocks = vi.hoisted(() => ({
  singles: {} as Record<string, Array<(payload: unknown) => void>>,
  many: [] as Array<(name: string, payload: unknown) => void>,
  fetch: vi.fn(),
  loader: vi.fn()
}));

vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => mocks.fetch(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useSignalREvent: (event: string, _subject: unknown, handler: (payload: unknown) => void) => {
    (mocks.singles[event] ??= []).push(handler);
  },
  useSignalREvents: (_events: readonly string[], _subject: unknown, handler: (name: string, payload: unknown) => void) => {
    mocks.many.push(handler);
  },
  useLiveEvent: () => undefined
}));

const { SearchCyclesPage } = await import("./search-cycles-page");

const films = {
  id: "lib-1", name: "Films", mediaType: "movies", rootPath: "D:/Films", downloadsPath: null, autoSearchEnabled: true, missingSearchEnabled: true,
  upgradeSearchEnabled: false, searchIntervalMinutes: 360, retryDelayHours: 6, maxItemsPerRun: 10, maxMissingItemsPerRun: 10, maxUpgradeItemsPerRun: 10,
  searchWindowStartHour: null, searchWindowEndHour: null, searchWindowDays: 127, randomizeSearchOrder: false
};

let settingsNow: Record<string, unknown> = {};

function loaderData() {
  return {
    automationStates: [{ libraryId: "lib-1", status: "idle", lastError: null, nextSearchUtc: null }], libraries: [films],
    qualityModel: { version: "1", tiers: [], upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: false }, updatedUtc: "2026-10-01T00:00:00Z" },
    settings: { ...emptyPlatformSettingsSnapshot, autoStartJobs: true, ...settingsNow }, searchCycles: [], blockedReleases: [], failureRules: [],
    recycleBinSettings: { retentionDays: 30, maxSizeMb: 102400 }, contentBlocklist: emptyContentBlocklistSettings
  };
}

function reply(body: unknown) {
  return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function open(path = "/search-cycles") {
  const router = createMemoryRouter([{ path: "/search-cycles/*", element: <SearchCyclesPage />, loader: () => { mocks.loader(); return loaderData(); } }], { initialEntries: [path] });
  render(<RouterProvider router={router} />);
}

const fire = (name: string, payload: unknown = null) => act(() => { for (const handler of mocks.many) handler(name, payload); });

function controlFor(label: string): HTMLElement {
  const element = screen.getAllByText(label).map((found) => found.closest("label")).find((candidate) => candidate !== null);
  const control = element?.htmlFor ? document.getElementById(element.htmlFor) : element?.querySelector("button, select, input");
  if (!control) throw new Error(`No control labelled ${label}`);
  return control as HTMLElement;
}

beforeEach(() => {
  settingsNow = {};
  mocks.singles = {};
  mocks.many = [];
  mocks.loader.mockReset();
  mocks.fetch.mockReset();
  mocks.fetch.mockImplementation(() => reply({ items: [], hasMore: false, nextPageToken: null }));
});

afterEach(() => vi.restoreAllMocks());

describe("the Automation screen follows the server", () => {
  it.each([["LibraryChanged"], ["SettingsChanged"], ["QualityProfileChanged"]])("a %s push reads the screen again, once, with no timer", async (event) => {
    open();
    await screen.findByText("Films");
    expect(mocks.loader).toHaveBeenCalledTimes(1);

    fire(event);
    fire(event);

    await waitFor(() => expect(mocks.loader).toHaveBeenCalledTimes(2), { timeout: 3000 });
    await new Promise((resolve) => setTimeout(resolve, 700));
    expect(mocks.loader, "two pushes in one burst read once").toHaveBeenCalledTimes(2);
  });

  it.each([[ConfigAreas.downloadHealth], [ConfigAreas.qualityModel]])("a change to the %s settings area reads the screen again and another area does not", async (area) => {
    open();
    await screen.findByText("Films");

    fire("ConfigChanged", { area: ConfigAreas.notifications });
    await new Promise((resolve) => setTimeout(resolve, 700));
    expect(mocks.loader).toHaveBeenCalledTimes(1);

    fire("ConfigChanged", { area });
    await waitFor(() => expect(mocks.loader).toHaveBeenCalledTimes(2), { timeout: 3000 });
  });

  it.each([["AutomationStateChanged"], ["SearchRunCompleted"]])("a %s push reads only the two schedule lists and the library's chip changes without a reload", async (event) => {
    open();
    const row = (await screen.findByText("Films")).closest("[role=row]") as HTMLElement;
    expect(within(row).getByText("Scheduled")).toBeVisible();
    mocks.fetch.mockImplementation((url: string) => {
      if (String(url).startsWith("/api/library-automation")) return reply({ items: [{ libraryId: "lib-1", status: "running", lastError: null, nextSearchUtc: null }], hasMore: false, nextPageToken: null });
      return reply({ items: [], hasMore: false, nextPageToken: null });
    });

    act(() => { for (const handler of mocks.singles[event] ?? []) handler(null); });

    await waitFor(() => expect(within(screen.getByText("Films").closest("[role=row]") as HTMLElement).getByText("Searching")).toBeVisible(), { timeout: 3000 });
    const asked = mocks.fetch.mock.calls.map(([url]) => String(url));
    expect(asked).toContain("/api/library-automation?pageSize=50");
    expect(asked).toContain("/api/search-cycles?pageSize=50");
    expect(mocks.loader, "the rest of the screen is not read again").toHaveBeenCalledTimes(1);
  });
});

describe("the screen adopts the server's newer settings only when the form has nothing unsaved", () => {
  it("shows a change made in another tab at once while the form is clean", async () => {
    open("/search-cycles/failed-downloads");
    await screen.findByText("Hourly search cap");
    expect(controlFor("Hourly search cap")).toHaveAttribute("aria-checked", "false");

    settingsNow = { maxSearchesPerHourEnabled: true, maxSearchesPerHour: 200 };
    fire("SettingsChanged");

    await waitFor(() => expect(controlFor("Hourly search cap")).toHaveAttribute("aria-checked", "true"), { timeout: 3000 });
  });

  it("keeps an edit in progress and does not overwrite it with the server's newer settings", async () => {
    open("/search-cycles/failed-downloads");
    await screen.findByText("Hourly search cap");
    fireEvent.click(controlFor("Download integrity check"));
    expect(controlFor("Download integrity check")).toHaveAttribute("aria-checked", "true");

    settingsNow = { maxSearchesPerHourEnabled: true, maxSearchesPerHour: 200 };
    fire("SettingsChanged");
    await waitFor(() => expect(mocks.loader).toHaveBeenCalledTimes(2), { timeout: 3000 });
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(controlFor("Download integrity check"), "the edit is kept").toHaveAttribute("aria-checked", "true");
    expect(controlFor("Hourly search cap"), "the other tab's change waits until the edit is saved or dropped").toHaveAttribute("aria-checked", "false");
  });
});
