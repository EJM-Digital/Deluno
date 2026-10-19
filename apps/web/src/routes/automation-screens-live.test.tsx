import { act, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import type { ReactElement } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ConfigAreas } from "../lib/config-areas";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * Retention, Keep seeding and Download health follow the server and nothing asks on a timer (#1179): a change made anywhere else
 * (another tab, an automatic pass) arrives as a push, and the screen reads its data again once, after a short settle. Each screen
 * listens to the events and Settings areas that can change what it shows, and to no others.
 */

const mocks = vi.hoisted(() => ({
  many: [] as Array<(name: string, payload: unknown) => void>,
  fetch: vi.fn(),
  loader: vi.fn()
}));

vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => mocks.fetch(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useSignalREvent: () => undefined,
  useSignalREvents: (events: readonly string[], _subject: unknown, handler: (name: string, payload: unknown) => void) => {
    mocks.many.push((name, payload) => { if (events.includes(name)) handler(name, payload); });
  },
  useLiveEvent: () => undefined
}));

const { RetentionPage } = await import("./retention-screen");
const { SettingsKeepSeedingPage } = await import("./settings-keep-seeding-page");
const { SettingsDownloadHealthPage } = await import("./settings-download-health-page");

const retentionData = { rules: [], servers: [], marks: [], keeps: [], fields: { movie: [], show: [], season: [], episode: [] } };
const keepSeedingData = { clients: [], indexers: [], tagCategories: [], problems: [] };
const downloadHealthData = { rules: [], ignoreEntries: [], settings: emptyPlatformSettingsSnapshot, connectivityCheckSkips: [] };

function open(path: string, element: ReactElement, data: unknown) {
  const router = createMemoryRouter([{ path, element, loader: () => { mocks.loader(); return data; } }], { initialEntries: [path] });
  render(<RouterProvider router={router} />);
}

const fire = (name: string, payload: unknown = null) => act(() => { for (const handler of mocks.many) handler(name, payload); });
const settle = () => new Promise((resolve) => setTimeout(resolve, 700));

beforeEach(() => {
  mocks.many = [];
  mocks.loader.mockReset();
  mocks.fetch.mockReset();
  mocks.fetch.mockImplementation(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve([]), clone() { return this; }, text: () => Promise.resolve("[]") } as unknown as Response));
});

const screens = [
  { name: "Retention", path: "/retention", element: <RetentionPage />, data: retentionData, heading: /Leaving soon/, events: ["LibraryChanged"], areas: [ConfigAreas.retention, ConfigAreas.mediaServers] },
  { name: "Keep seeding", path: "/keep-seeding", element: <SettingsKeepSeedingPage />, data: keepSeedingData, heading: /Nothing here is on/, events: ["DownloadHealthChanged"], areas: [ConfigAreas.downloadHealth] },
  { name: "Download health", path: "/download-health", element: <SettingsDownloadHealthPage />, data: downloadHealthData, heading: /Download health|strike/i, events: ["SettingsChanged"], areas: [ConfigAreas.downloadHealth] }
];

describe.each(screens)("$name follows the server", ({ path, element, data, heading, events, areas }) => {
  it.each(events)("a %s push reads the screen again, once", async (event) => {
    open(path, element, data);
    await screen.findAllByText(heading);
    expect(mocks.loader).toHaveBeenCalledTimes(1);

    fire(event);
    fire(event);

    await waitFor(() => expect(mocks.loader).toHaveBeenCalledTimes(2), { timeout: 3000 });
    await settle();
    expect(mocks.loader, "two pushes in one burst read once").toHaveBeenCalledTimes(2);
  });

  it.each(areas)("a change to the %s settings area reads the screen again", async (area) => {
    open(path, element, data);
    await screen.findAllByText(heading);

    fire("ConfigChanged", { area });

    await waitFor(() => expect(mocks.loader).toHaveBeenCalledTimes(2), { timeout: 3000 });
  });

  it("a change to a settings area it does not show, and an event about something else, read nothing", async () => {
    open(path, element, data);
    await screen.findAllByText(heading);

    fire("ConfigChanged", { area: ConfigAreas.notifications });
    fire("MonitoringChanged");
    await settle();

    expect(mocks.loader).toHaveBeenCalledTimes(1);
  });
});
