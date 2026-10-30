import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { IntakeSourceItem, LibraryItem, TraktConnectionStatus } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Import lists, the Behaviour part of the drawer (#1329): the "Check the list" interval presets and a custom number of
 * hours, and the tags a list gives the titles it adds.
 */

vi.setConfig({ testTimeout: 30_000 });
const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson: fetchJsonMock }));
vi.mock("../lib/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson: fetchJsonMock }));
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));

const library = (id: string, name: string, mediaType: string) => ({ id, name, mediaType }) as unknown as LibraryItem;
const list = (over: Partial<IntakeSourceItem> = {}): IntakeSourceItem => ({
  id: "l1", name: "Weekend list", provider: "rss", feedUrl: "https://lists.example.test/a.xml", mediaType: "movies", libraryId: "lib-m", libraryName: "Films",
  qualityProfileId: null, qualityProfileName: null, requiredGenres: "", minimumRating: null, minimumYear: null, maximumAgeDays: null, allowedCertifications: "",
  audience: "any", syncIntervalHours: 12, lastSyncUtc: null, lastSyncStatus: "never", lastSyncSummary: null, searchOnAdd: false, isEnabled: true,
  createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", tags: "", ...over
});
const loader = {
  intakeSources: [] as IntakeSourceItem[],
  libraries: [library("lib-m", "Films", "movies")],
  exclusions: [],
  traktStatus: { clientConfigured: false, connected: false, expiresUtc: null, needsReconnect: false } as TraktConnectionStatus,
  settings: { tagTitlesOffEveryList: false, offListTag: "" } as Record<string, unknown>
};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loader,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsListsPage } = await import("./settings-lists-page");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const puts = () => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "PUT");
const bodyOf = (call: unknown[]) => JSON.parse((call[1] as RequestInit).body as string);

beforeEach(() => {
  cleanup();
  loader.intakeSources = [list()];
  fetchJsonMock.mockReset();
  fetchJsonMock.mockImplementation(async (url: string) => (url === "/api/tags" ? [{ id: "t1", name: "Kids", color: "blue" }, { id: "t2", name: "Weekend", color: "green" }] : []));
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async () => json(list()));
});

async function openDrawer() {
  render(<MemoryRouter><SettingsListsPage /></MemoryRouter>);
  fireEvent.click(screen.getByText("Weekend list"));
  return await screen.findByRole("dialog");
}
async function save() {
  fireEvent.click(screen.getByRole("button", { name: "Save list" }));
  await waitFor(() => expect(puts()).toHaveLength(1));
  return bodyOf(puts()[0]!);
}
const intervalSelect = (dialog: HTMLElement) => within(dialog).getByRole("combobox", { name: /Check the list/ }) as HTMLSelectElement;

describe("the Check the list interval", () => {
  it("offers every 6 hours, every 12 hours, daily, every 3 days and weekly, each saved as its hours", async () => {
    const presets = [["Every 6 hours", 6], ["Every 12 hours", 12], ["Daily", 24], ["Every 3 days", 72], ["Weekly", 168]] as const;
    const dialog = await openDrawer();
    expect(Array.from(intervalSelect(dialog).options).map((option) => option.text)).toEqual(expect.arrayContaining(["Every 6 hours", "Every 12 hours", "Daily", "Every 3 days", "Weekly", "Custom interval"]));
    cleanup();

    for (const [label, hours] of presets) {
      vi.mocked(authedFetch).mockClear();
      // The list starts at 12 hours, so a pick of 12 changes nothing to save; it must show as the chosen preset.
      loader.intakeSources = [list({ syncIntervalHours: hours === 12 ? 24 : 12 })];
      const opened = await openDrawer();
      const select = intervalSelect(opened);
      fireEvent.change(select, { target: { value: Array.from(select.options).find((option) => option.text === label)!.value } });
      expect(select.selectedOptions[0]!.text).toBe(label);
      expect((await save()).syncIntervalHours).toBe(hours);
      cleanup();
    }
  });

  it("a custom interval takes a number of hours and saves it", async () => {
    const dialog = await openDrawer();
    const select = intervalSelect(dialog);

    fireEvent.change(select, { target: { value: Array.from(select.options).find((option) => option.text === "Custom interval")!.value } });
    fireEvent.change(within(dialog).getByPlaceholderText("Hours"), { target: { value: "36" } });

    expect((await save()).syncIntervalHours).toBe(36);
  });
});

describe("the tags a list gives the titles it adds", () => {
  it("picking a tag in the drawer is saved with the list, a list with tags keeps them when saved untouched, and none is saved as none", async () => {
    const dialog = await openDrawer();
    fireEvent.click(within(dialog).getByRole("textbox", { name: "Tags for titles this list adds" }));
    fireEvent.click(await within(dialog).findByRole("button", { name: "Add Kids to Tags for titles this list adds" }));
    expect((await save()).tags).toBe("Kids");
    cleanup();

    vi.mocked(authedFetch).mockClear();
    loader.intakeSources = [list({ tags: "Weekend" })];
    const tagged = await openDrawer();
    expect(within(tagged).getByText("Weekend")).toBeInTheDocument();
    fireEvent.click(within(tagged).getByRole("switch", { name: /Search when a title is added/ }));
    expect((await save()).tags).toBe("Weekend");
    cleanup();

    vi.mocked(authedFetch).mockClear();
    loader.intakeSources = [list({ tags: "" })];
    const bare = await openDrawer();
    fireEvent.click(within(bare).getByRole("switch", { name: /Search when a title is added/ }));
    expect((await save()).tags).toBe("");
  });
});

describe("what a sync leaves on the list", () => {
  it("shows when it last ran and how it ended in the table and in the drawer, and Never and Not synced yet before the first", async () => {
    loader.intakeSources = [list({ id: "a", name: "Never synced list" }), list({ id: "b", name: "Synced list", lastSyncUtc: new Date(Date.now() - 5 * 60_000).toISOString(), lastSyncStatus: "success", lastSyncSummary: "Checked 12 titles, added 2." })];
    render(<MemoryRouter><SettingsListsPage /></MemoryRouter>);

    const never = within(screen.getAllByRole("row").find((row) => row.textContent?.includes("Never synced list"))!);
    expect(never.getByText("Never")).toBeInTheDocument();
    expect(never.getByText("Not synced yet")).toBeInTheDocument();
    const synced = within(screen.getAllByRole("row").find((row) => row.textContent?.includes("Synced list") && !row.textContent.includes("Never synced list"))!);
    expect(synced.getByText("Checked 12 titles, added 2.")).toBeInTheDocument();
    expect(synced.queryByText("Never")).toBeNull();

    fireEvent.click(screen.getByText("Synced list"));
    const drawer = await screen.findByRole("dialog");
    expect(drawer).toHaveTextContent(/last sync .*5.*· success/);
    expect(within(drawer).getByText("Checked 12 titles, added 2.")).toBeInTheDocument();
  });
});
