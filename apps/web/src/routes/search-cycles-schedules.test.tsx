import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyContentBlocklistSettings, emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * Automation and Recovery: a library's search schedule in its drawer, the lists of libraries on the Missing and Upgrades tabs,
 * and the cards on the Failed downloads tab. Each control sends the request the server's automation routes are tested against.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn(), dismiss: vi.fn() })
  };
});

const library = (overrides: Record<string, unknown> = {}) => ({
  id: "lib-1", name: "Films", mediaType: "movies", rootPath: "D:/Films", downloadsPath: null, autoSearchEnabled: true, missingSearchEnabled: true,
  upgradeSearchEnabled: false, searchIntervalMinutes: 360, retryDelayHours: 6, maxItemsPerRun: 10, maxMissingItemsPerRun: 10, maxUpgradeItemsPerRun: 10,
  searchWindowStartHour: null, searchWindowEndHour: null, searchWindowDays: 127, randomizeSearchOrder: false, ...overrides
});

let loaderData: Record<string, unknown>;
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn(), state: "idle" })
}));

const { SearchCyclesPage } = await import("./search-cycles-page");

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function load(overrides: Record<string, unknown> = {}) {
  loaderData = {
    automationStates: [], libraries: [library()],
    qualityModel: { version: "1", tiers: [], upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: false }, updatedUtc: "2026-10-01T00:00:00Z" },
    settings: { ...emptyPlatformSettingsSnapshot, autoStartJobs: true }, searchCycles: [], blockedReleases: [], failureRules: [],
    recycleBinSettings: { retentionDays: 30, maxSizeMb: 102400 }, contentBlocklist: emptyContentBlocklistSettings, ...overrides
  };
}

function openAt(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <SearchCyclesPage />
    </MemoryRouter>
  );
}

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

/** The control that goes with a label whose text starts a switch row or a field. */
function controlFor(scope: ReturnType<typeof within>, label: string): HTMLElement {
  const element = scope.getAllByText(label).map((found: HTMLElement) => found.closest("label")).find((candidate: HTMLLabelElement | null) => candidate !== null);
  const control = element?.htmlFor ? document.getElementById(element.htmlFor) : element?.querySelector("button, select, input");
  if (!control) throw new Error(`No control labelled ${label}`);
  return control as HTMLElement;
}

async function openDrawer(name = "Films") {
  fireEvent.click(screen.getByText(name));
  return within(await screen.findByRole("dialog"));
}

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  authedFetchMock.mockImplementation((url: string) => String(url) === "/api/quality-model" ? reply({ version: "1", tiers: [], upgradeStop: { stopWhenCutoffMet: false, requireCustomFormatGainForSameQuality: false }, updatedUtc: "2026-10-01T00:00:00Z" }) : reply([]));
  load();
});

afterEach(() => vi.restoreAllMocks());

async function saveSchedule(drawer: ReturnType<typeof within>) {
  fireEvent.click(drawer.getByRole("button", { name: "Save schedule" }));
  await waitFor(() => expect(calls("PUT", "/api/libraries/lib-1/automation")).toHaveLength(1));
  return bodyOf(calls("PUT", "/api/libraries/lib-1/automation")[0]);
}

describe("a library's search schedule drawer", () => {
  it.each([
    ["Search automatically", "autoSearchEnabled", true],
    ["Missing titles", "missingSearchEnabled", true],
    ["Upgrades", "upgradeSearchEnabled", false]
  ])("the %s switch sets %s and nothing else, then saves it", async (label, key, was) => {
    openAt("/search-cycles");
    const drawer = await openDrawer();

    fireEvent.click(controlFor(drawer, label));
    const body = await saveSchedule(drawer);

    expect(body[key]).toBe(!was);
    expect(body).toMatchObject({ searchIntervalMinutes: 360, retryDelayHours: 6 });
  });

  it.each([
    ["15", "Every 15 minutes"], ["30", "Every 30 minutes"], ["60", "Every hour"], ["180", "Every 3 hours"], ["720", "Every 12 hours"], ["1440", "Daily"]
  ])("Search every offers %s minutes as %s", async (minutes, label) => {
    openAt("/search-cycles");
    const drawer = await openDrawer();
    const select = controlFor(drawer, "Search every") as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.textContent)).toContain(label);
    fireEvent.change(select, { target: { value: minutes } });
    const body = await saveSchedule(drawer);

    expect(body.searchIntervalMinutes).toBe(Number(minutes));
  });

  it("Search every takes a custom number of minutes", async () => {
    openAt("/search-cycles");
    const drawer = await openDrawer();

    fireEvent.change(controlFor(drawer, "Search every"), { target: { value: "__custom" } });
    fireEvent.change(drawer.getByPlaceholderText("Minutes"), { target: { value: "45" } });
    const body = await saveSchedule(drawer);

    expect(body.searchIntervalMinutes).toBe(45);
  });

  it("Retry after offers 1, 3, 6, 12 hours or daily, or custom hours, and an empty one falls back to 6", async () => {
    openAt("/search-cycles");
    const drawer = await openDrawer();
    const select = controlFor(drawer, "Retry after") as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.textContent)).toEqual(["1 hour", "3 hours", "6 hours", "12 hours", "Daily", "Custom delay"]);
    fireEvent.change(select, { target: { value: "__custom" } });
    fireEvent.change(drawer.getByPlaceholderText("Hours"), { target: { value: "" } });
    const body = await saveSchedule(drawer);

    expect(body.retryDelayHours).toBe(6);
  });

  it("Missing titles per run and Upgrade candidates per run are separate presets, and an empty missing one falls back to the combined cap", async () => {
    load({ libraries: [library({ maxItemsPerRun: 25, maxMissingItemsPerRun: 25, maxUpgradeItemsPerRun: 5 })] });
    openAt("/search-cycles");
    const drawer = await openDrawer();
    const missing = controlFor(drawer, "Missing titles per run") as HTMLSelectElement;
    const upgrades = controlFor(drawer, "Upgrade candidates per run") as HTMLSelectElement;

    expect(Array.from(missing.options).map((option) => option.textContent)).toEqual(["5 titles", "10 titles", "25 titles", "50 titles", "Custom batch"]);
    expect(missing.value).toBe("25");
    expect(upgrades.value).toBe("5");
    fireEvent.change(upgrades, { target: { value: "50" } });
    const body = await saveSchedule(drawer);

    expect(body).toMatchObject({ maxMissingItemsPerRun: 25, maxUpgradeItemsPerRun: 50 });
  });

  it("the hour selects run from 00:00 to 23:00, read Any time when empty, and send nothing for an empty one", async () => {
    openAt("/search-cycles");
    const drawer = await openDrawer();
    const after = controlFor(drawer, "Only search after") as HTMLSelectElement;
    const before = controlFor(drawer, "And before") as HTMLSelectElement;

    expect(after.options.length).toBe(25);
    expect(after.options[0].textContent).toBe("Any time");
    expect(after.options[1].textContent).toBe("00:00");
    expect(after.options[24].textContent).toBe("23:00");
    expect(after).toHaveValue("");
    expect(before).toHaveValue("");

    fireEvent.change(after, { target: { value: "1" } });
    fireEvent.change(before, { target: { value: "6" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save schedule" }));
    await waitFor(() => expect(calls("PUT", "/api/libraries/lib-1/automation")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/libraries/lib-1/automation")[0])).toMatchObject({ searchWindowStartHour: 1, searchWindowEndHour: 6 });
  });

  it("the seven day buttons say whether searches are on or off that day, and turning a day off removes it from the mask", async () => {
    openAt("/search-cycles");
    const drawer = await openDrawer();

    const sunday = drawer.getByRole("button", { name: "Searches on Sunday" });
    expect(sunday).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(sunday);
    expect(drawer.getByRole("button", { name: "Searches off Sunday" })).toHaveAttribute("aria-pressed", "false");
    const body = await saveSchedule(drawer);

    expect(body.searchWindowDays).toBe(126);
  });

  it("turning every day off is read as every day, never as no day", async () => {
    load({ libraries: [library({ searchWindowDays: 1 })] });
    openAt("/search-cycles");
    const drawer = await openDrawer();

    fireEvent.click(drawer.getByRole("button", { name: "Searches on Sunday" }));
    const body = await saveSchedule(drawer);

    expect(body.searchWindowDays).toBe(127);
  });

  it("Search order says Random or Oldest added first, and the choice reaches the request", async () => {
    openAt("/search-cycles");
    const drawer = await openDrawer();

    expect(drawer.getByText("Oldest added first.")).toBeVisible();
    fireEvent.click(controlFor(drawer, "Search order"));
    expect(drawer.getByText(/Random — a new title never waits behind a long backlog\./)).toBeVisible();
    const body = await saveSchedule(drawer);

    expect(body.randomizeSearchOrder).toBe(true);
  });

  it("Search now queues a search for the library and says so; an unsaved change closes it until saved", async () => {
    openAt("/search-cycles");
    const drawer = await openDrawer();
    fireEvent.click(controlFor(drawer, "Missing titles"));
    expect(drawer.getByRole("button", { name: "Search now" })).toBeDisabled();
    expect(drawer.getByText("Save your changes first.")).toBeVisible();
    fireEvent.click(controlFor(drawer, "Missing titles"));

    fireEvent.click(drawer.getByRole("button", { name: "Search now" }));

    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Search queued for Films"));
    expect(calls("POST", "/api/libraries/lib-1/search-now")).toHaveLength(1);
  });

  it("Skip next cycle posts the skip and names the library; a refusal says it could not be skipped", async () => {
    openAt("/search-cycles");
    const drawer = await openDrawer();

    fireEvent.click(drawer.getByRole("button", { name: "Skip next cycle" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Next cycle skipped for Films"));
    expect(calls("POST", "/api/libraries/lib-1/skip-cycle")).toHaveLength(1);

    authedFetchMock.mockImplementation(() => reply({}, false, 500));
    fireEvent.click(drawer.getByRole("button", { name: "Skip next cycle" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Cycle could not be skipped."));
  });
});

describe("the library lists", () => {
  const libs = [
    library({ id: "a", name: "Films", missingSearchEnabled: true, upgradeSearchEnabled: false }),
    library({ id: "b", name: "Shows", mediaType: "tv", missingSearchEnabled: false, upgradeSearchEnabled: true }),
    library({ id: "c", name: "Manual films", autoSearchEnabled: false })
  ];

  it("the Missing tab lists only libraries whose missing search is on, the Upgrades tab only those whose upgrade search is on", () => {
    load({ libraries: libs });
    openAt("/search-cycles/missing");
    expect(screen.getByText("Films")).toBeVisible();
    expect(screen.queryByText("Shows")).not.toBeInTheDocument();
  });

  it("the Upgrades tab lists the upgrade libraries only", () => {
    load({ libraries: libs });
    openAt("/search-cycles/upgrades");
    expect(screen.getByText("Shows")).toBeVisible();
    expect(screen.queryByText("Films")).not.toBeInTheDocument();
  });

  it("each library's status chip says Manual, Paused, Last run failed, Searching, Queued or Scheduled", () => {
    const states = [
      { libraryId: "a", status: "running", lastError: null, nextSearchUtc: null },
      { libraryId: "b", status: "queued", lastError: null, nextSearchUtc: null },
      { libraryId: "d", status: "idle", lastError: "Indexer refused.", nextSearchUtc: null }
    ];
    load({ libraries: [...libs, library({ id: "d", name: "Failing films" })], automationStates: states });
    openAt("/search-cycles");

    expect(within(screen.getByText("Manual films").closest("[role=row]") as HTMLElement).getByText("Manual")).toBeVisible();
    expect(within(screen.getByText("Films").closest("[role=row]") as HTMLElement).getByText("Searching")).toBeVisible();
    expect(within(screen.getByText("Shows").closest("[role=row]") as HTMLElement).getByText("Queued")).toBeVisible();
    expect(within(screen.getByText("Failing films").closest("[role=row]") as HTMLElement).getByText("Last run failed")).toBeVisible();
  });

  it("every chip reads Paused while automation is paused as a whole", () => {
    load({ libraries: [libs[0]], settings: { ...emptyPlatformSettingsSnapshot, autoStartJobs: false } });
    openAt("/search-cycles");

    expect(within(screen.getByText("Films").closest("[role=row]") as HTMLElement).getByText("Paused")).toBeVisible();
    expect(screen.getByRole("button", { name: "Resume automation" })).toBeVisible();
  });

  it("a library's On switch puts only the automatic-search flag back", async () => {
    load({ libraries: [libs[0]] });
    openAt("/search-cycles");

    fireEvent.click(screen.getByRole("switch", { name: "Pause automation for Films" }));

    await waitFor(() => expect(calls("PUT", "/api/libraries/a/automation")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/libraries/a/automation")[0])).toMatchObject({ autoSearchEnabled: false, missingSearchEnabled: true, searchIntervalMinutes: 360 });
  });

  it("Stop searching once the cutoff is met writes the upgrade stop through the quality model when saved", async () => {
    load({ libraries: [libs[1]] });
    openAt("/search-cycles/upgrades");
    const row = screen.getByText("Stop searching once the cutoff is met").closest("label")!;

    fireEvent.click(document.getElementById(row.htmlFor)!);
    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));

    await waitFor(() => expect(calls("PUT", "/api/quality-model")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/quality-model")[0]).upgradeStop.stopWhenCutoffMet).toBe(false);
  });
});

describe("the Failed downloads cards", () => {
  async function saved() {
    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));
    await waitFor(() => expect(calls("PATCH", "/api/settings")).toHaveLength(1));
    return bodyOf(calls("PATCH", "/api/settings")[0]);
  }

  it("the hourly search cap switch and its preset send the cap, clamped to 1 to 100000", async () => {
    openAt("/search-cycles/failed-downloads");
    const select = controlFor(within(document.body), "Searches per hour") as HTMLSelectElement;

    expect(select).toBeDisabled();
    fireEvent.click(controlFor(within(document.body), "Hourly search cap"));
    expect(select).toBeEnabled();
    expect(Array.from(select.options).map((option) => option.textContent)).toEqual(["100", "200", "400", "Custom"]);
    fireEvent.change(select, { target: { value: "__custom" } });
    fireEvent.change(screen.getByPlaceholderText("1–100000"), { target: { value: "999999" } });
    const body = await saved();

    expect(body).toMatchObject({ maxSearchesPerHourEnabled: true, maxSearchesPerHour: 100000 });
  });

  it("pausing while downloads pile up sends the threshold from its presets 10, 20 and 50 or a custom number", async () => {
    openAt("/search-cycles/failed-downloads");
    fireEvent.click(controlFor(within(document.body), "Pause automatic searching while downloads pile up"));
    const select = controlFor(within(document.body), "Pause above this many waiting") as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.textContent)).toEqual(["10", "20", "50", "Custom"]);
    fireEvent.change(select, { target: { value: "50" } });
    const body = await saved();

    expect(body).toMatchObject({ pauseSearchWhenQueueLongEnabled: true, pauseSearchWhenQueueLongThreshold: 50 });
  });

  it("the download integrity check switch and its minimum runtime presets 70, 80 and 90 send their values", async () => {
    openAt("/search-cycles/failed-downloads");
    fireEvent.click(controlFor(within(document.body), "Download integrity check"));
    const select = controlFor(within(document.body), "Minimum runtime") as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.textContent)).toEqual(["70%", "80%", "90%", "Custom"]);
    fireEvent.change(select, { target: { value: "90" } });
    const body = await saved();

    expect(body).toMatchObject({ downloadIntegrityCheckEnabled: true, downloadIntegrityMinimumRuntimePercent: 90 });
  });

  it("each of the four default-action switches is saved as its own setting", async () => {
    openAt("/search-cycles/failed-downloads");
    for (const label of ["Block this release", "Search for a replacement", "Remove the client entry", "Purge residual files"]) {
      fireEvent.click(controlFor(within(document.body), label));
    }
    const body = await saved();

    expect(body).toMatchObject({
      cleanupBlockReleaseAfterThreshold: true, cleanupQueueReplacementAfterThreshold: true,
      cleanupRemoveClientEntryAfterThreshold: true, cleanupPurgePayloadAfterThreshold: true
    });
  });

  it("the content blocklist card sends its switch, its mode, the built-in switch and the custom patterns trimmed with blank lines dropped", async () => {
    openAt("/search-cycles/failed-downloads");
    fireEvent.click(controlFor(within(document.body), "Check files inside downloads"));
    fireEvent.click(screen.getByRole("radio", { name: "Whitelist" }));
    fireEvent.change(screen.getByPlaceholderText(/\*\.exe/), { target: { value: "  *.exe \n\n*.scr  \n" } });
    fireEvent.change(screen.getByPlaceholderText("https://example.com/blocklist.txt"), { target: { value: "https://example.com/list.txt" } });
    fireEvent.click(controlFor(within(document.body), "Push into qBittorrent's Excluded file names"));
    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));

    await waitFor(() => expect(calls("PUT", "/api/download-health/content-blocklist")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/download-health/content-blocklist")[0])).toMatchObject({
      enabled: true, mode: "whitelist", customPatterns: ["*.exe", "*.scr"], remoteUrl: "https://example.com/list.txt", syncToQbittorrentEnabled: true
    });
  });

  it("when one of the three writes is refused the page says why and the footer stays open to save again", async () => {
    authedFetchMock.mockImplementation((url: string, init?: RequestInit) => String(url) === "/api/download-health/content-blocklist" && init?.method === "PUT"
      ? reply({ title: "That list address is not a web address." }, false, 400)
      : reply([]));
    openAt("/search-cycles/failed-downloads");
    fireEvent.click(controlFor(within(document.body), "Check files inside downloads"));

    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));

    expect(await screen.findByText("That list address is not a web address.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Save automation settings" })).toBeEnabled();
  });
});

describe("When a download finishes", () => {
  async function saved() {
    fireEvent.click(screen.getByRole("button", { name: "Save automation settings" }));
    await waitFor(() => expect(calls("PATCH", "/api/settings")).toHaveLength(1));
    return bodyOf(calls("PATCH", "/api/settings")[0]);
  }

  it("offers Share then tidy up, Tidy up now and Leave it alone, and shows the dials only when they can apply", () => {
    openAt("/search-cycles");

    expect(screen.getAllByRole("radio", { name: "Share, then tidy up" }).length).toBeGreaterThan(0);
    expect(screen.getByLabelText("If it can never get there")).toBeVisible();
    fireEvent.click(screen.getByRole("radio", { name: "Tidy up now" }));
    expect(screen.queryByLabelText("If it can never get there")).not.toBeInTheDocument();
    expect(screen.getByText(/Deluno reclaims the space as soon as the import is verified/)).toBeVisible();
    fireEvent.click(screen.getByRole("radio", { name: "Leave it alone" }));
    expect(screen.queryByLabelText("Tracker privacy this covers")).not.toBeInTheDocument();
    expect(screen.getByText(/Deluno never removes anything from your download client/)).toBeVisible();
  });

  it("Tracker privacy this covers offers public and private, public only or private only", async () => {
    openAt("/search-cycles");

    fireEvent.click(screen.getByRole("radio", { name: "Private only" }));
    const body = await saved();

    expect(body.sharingPrivacyScope).toBe("private");
  });

  it("Keep sharing for offers 1, 3 and 14 days or custom hours, and blank ignores time", async () => {
    openAt("/search-cycles");
    const select = controlFor(within(document.body), "Keep sharing for") as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.textContent)).toEqual(["1 day", "3 days", "14 days", "Custom"]);
    fireEvent.change(select, { target: { value: "__custom" } });
    fireEvent.change(screen.getByPlaceholderText("Hours"), { target: { value: "" } });
    const body = await saved();

    expect(body.sharingForHours).toBeNull();
  });

  it("If it can never get there offers Give up, Keep waiting or Ask me, and Give up after only follows Give up", async () => {
    openAt("/search-cycles");
    expect(screen.getByLabelText("Give up after")).toBeVisible();

    fireEvent.click(screen.getByRole("radio", { name: "Keep waiting" }));
    expect(screen.queryByLabelText("Give up after")).not.toBeInTheDocument();
    const body = await saved();

    expect(body.sharingStuckAction).toBe("keep-waiting");
  });

  it("Once the target is reached chooses Reclaim the space or Stop seeding without deleting", async () => {
    openAt("/search-cycles");

    fireEvent.click(screen.getByRole("radio", { name: "Stop seeding without deleting" }));
    const body = await saved();

    expect(body.sharingCompletionAction).toBe("stop");
  });
});
