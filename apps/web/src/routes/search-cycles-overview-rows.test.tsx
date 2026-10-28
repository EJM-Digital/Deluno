import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { emptyContentBlocklistSettings, emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * Automation overview: the parts of the strip, the library rows and the Recent cycles card that need data a fresh server cannot
 * produce (a search that called an indexer and sent a release, a search that failed, a cycle from the scheduler or an upgrade sweep).
 * The page is the real one; the loader's answer is what the server's lists return, put in by hand.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return { ...original, toast: Object.assign(() => undefined, { success: vi.fn(), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn(), dismiss: vi.fn() }) };
});

const library = (overrides: Record<string, unknown> = {}) => ({
  id: "lib-1", name: "Films", mediaType: "movies", rootPath: "D:/Films", downloadsPath: null, autoSearchEnabled: true, missingSearchEnabled: true,
  upgradeSearchEnabled: true, searchIntervalMinutes: 360, retryDelayHours: 6, maxItemsPerRun: 10, maxMissingItemsPerRun: 10, maxUpgradeItemsPerRun: 10,
  searchWindowStartHour: null, searchWindowEndHour: null, searchWindowDays: 127, randomizeSearchOrder: false, ...overrides
});
const minutesFromNow = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();
const state = (overrides: Record<string, unknown> = {}) => ({
  libraryId: "lib-1", libraryName: "Films", mediaType: "movies", status: "idle", searchRequested: false, lastPlannedUtc: null, lastStartedUtc: null,
  lastCompletedUtc: null, nextSearchUtc: null, lastJobId: null, lastError: null, updatedUtc: new Date().toISOString(), nextMissingSearchUtc: null,
  nextUpgradeSearchUtc: null, nextSubtitleSearchUtc: null, ...overrides
});
let cycleNumber = 0;
const cycle = (overrides: Record<string, unknown> = {}) => {
  cycleNumber += 1;
  return {
    id: `cycle-${cycleNumber}`, libraryId: "lib-1", libraryName: "Films", mediaType: "movies", triggerKind: "manual", status: "completed", plannedCount: 0, queuedCount: 0,
    skippedCount: 0, notesJson: JSON.stringify({ apiCallCount: 0, queuedReleaseBytes: 0 }), startedUtc: minutesFromNow(-10 - cycleNumber), completedUtc: minutesFromNow(-9 - cycleNumber),
    searchKind: "combined", ...overrides
  };
};

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
function openAt(path = "/search-cycles") {
  window.localStorage.clear();
  render(<MemoryRouter initialEntries={[path]}><SearchCyclesPage /></MemoryRouter>);
}
const stripCell = (label: string) => screen.getByText(label, { exact: true }).parentElement as HTMLElement;
const libraryRow = (name: string) => screen.getAllByRole("row").find((candidate) => candidate.textContent?.includes(name) && !candidate.textContent.includes("queued"))!;

beforeEach(() => {
  authedFetchMock.mockReset();
  toastError.mockReset();
  authedFetchMock.mockImplementation(() => reply([]));
  cycleNumber = 0;
  load();
});
afterEach(() => vi.restoreAllMocks());

describe("Automation overview strip", () => {
  it("Source checks sums apiCallCount from every cycle's notes and Sent to downloads formats the queued bytes of all of them", () => {
    load({
      searchCycles: [
        cycle({ notesJson: JSON.stringify({ apiCallCount: 1200, queuedReleaseBytes: 700 }) }),
        cycle({ notesJson: JSON.stringify({ apiCallCount: 34, queuedReleaseBytes: 1_572_864 }) }),
        cycle({ notesJson: "not json" }),
        cycle({ notesJson: null })
      ]
    });
    openAt();
    expect(within(stripCell("Source checks")).getByText((1234).toLocaleString())).toBeInTheDocument();
    expect(within(stripCell("Source checks")).getByText("in the last 50 cycles")).toBeInTheDocument();
    // 700 B + 1.5 MB = 1,573,564 B, which reads as 1.5 MB.
    expect(within(stripCell("Sent to downloads")).getByText("1.5 MB")).toBeInTheDocument();
  });

  it("Sent to downloads reads in B, KB, MB, GB and TB as the total grows", () => {
    for (const [bytes, text] of [[512, "512 B"], [2048, "2.0 KB"], [5 * 1024 ** 2, "5.0 MB"], [3 * 1024 ** 3, "3.0 GB"], [2 * 1024 ** 4, "2.0 TB"]] as const) {
      load({ searchCycles: [cycle({ notesJson: JSON.stringify({ apiCallCount: 1, queuedReleaseBytes: bytes }) })] });
      const { unmount } = render(<MemoryRouter initialEntries={["/search-cycles"]}><SearchCyclesPage /></MemoryRouter>);
      expect(within(stripCell("Sent to downloads")).getByText(text)).toBeInTheDocument();
      unmount();
    }
  });

  it("Searching now counts the libraries whose status is running and says they are mid-cycle", () => {
    load({
      libraries: [library(), library({ id: "lib-2", name: "Shows", mediaType: "tv" }), library({ id: "lib-3", name: "Docs" })],
      automationStates: [state({ status: "running" }), state({ libraryId: "lib-2", libraryName: "Shows", status: "running" }), state({ libraryId: "lib-3", libraryName: "Docs", status: "idle", nextSearchUtc: minutesFromNow(90) })]
    });
    openAt();
    expect(within(stripCell("Searching now")).getByText("2")).toBeInTheDocument();
    expect(within(stripCell("Searching now")).getByText("libraries mid-cycle")).toBeInTheDocument();
    expect(within(stripCell("Due")).getByText("2")).toBeInTheDocument();
  });

  it("Due leaves out a paused library and one whose next search is still ahead, and counts one whose next search has passed", () => {
    load({
      libraries: [library(), library({ id: "lib-2", name: "Shows", mediaType: "tv" }), library({ id: "lib-3", name: "Docs" }), library({ id: "lib-4", name: "Home" })],
      automationStates: [
        state({ nextSearchUtc: minutesFromNow(-5) }),
        state({ libraryId: "lib-2", libraryName: "Shows", status: "paused", nextSearchUtc: minutesFromNow(-5) }),
        state({ libraryId: "lib-3", libraryName: "Docs", nextSearchUtc: minutesFromNow(60) }),
        state({ libraryId: "lib-4", libraryName: "Home", nextSearchUtc: null })
      ]
    });
    openAt();
    expect(within(stripCell("Due")).getByText("2")).toBeInTheDocument();
    expect(within(stripCell("Due")).getByText("libraries ready for a cycle")).toBeInTheDocument();
  });
});

describe("Automation overview library rows", () => {
  it("Next / last reads the missing and upgrade times together, Due now once one has passed, and only the missing time on the Missing tab", () => {
    load({ automationStates: [state({ nextMissingSearchUtc: minutesFromNow(12), nextUpgradeSearchUtc: minutesFromNow(180) })] });
    openAt();
    expect(libraryRow("Films").textContent).toContain("Missing in 12 min · Upgrades in 3 h");
  });

  it("Next / last reads Due now for a time that has passed, on each tab", () => {
    load({ automationStates: [state({ nextMissingSearchUtc: minutesFromNow(-3), nextUpgradeSearchUtc: minutesFromNow(-1) })] });
    openAt("/search-cycles/missing");
    expect(libraryRow("Films").textContent).toContain("Missing Due now");
    expect(libraryRow("Films").textContent).not.toContain("Upgrades Due now");
    expect(libraryRow("Films").textContent).not.toContain("Upgrades in");
  });

  it("Next / last on the Upgrades tab reads only the upgrade time", () => {
    load({ automationStates: [state({ nextMissingSearchUtc: minutesFromNow(12), nextUpgradeSearchUtc: minutesFromNow(180) })] });
    openAt("/search-cycles/upgrades");
    expect(libraryRow("Films").textContent).toContain("Upgrades in 3 h");
    expect(libraryRow("Films").textContent).not.toContain("Missing in");
  });

  it("the second line is the last error when there is one, else when it last ran, else Not run yet", () => {
    load({
      libraries: [library(), library({ id: "lib-2", name: "Shows", mediaType: "tv" }), library({ id: "lib-3", name: "Docs" })],
      automationStates: [
        state({ lastError: "No indexer answered", lastCompletedUtc: minutesFromNow(-30), nextSearchUtc: minutesFromNow(60) }),
        state({ libraryId: "lib-2", libraryName: "Shows", lastCompletedUtc: minutesFromNow(-30), nextSearchUtc: minutesFromNow(60) })
      ]
    });
    openAt();
    expect(libraryRow("Films").textContent).toContain("No indexer answered");
    expect(libraryRow("Films").textContent).not.toContain("Last ran");
    expect(libraryRow("Films").textContent).toContain("Last run failed");
    expect(libraryRow("Shows").textContent).toContain("Last ran 30 min ago");
    expect(libraryRow("Docs").textContent).toContain("Not run yet");
  });

  it("Run now shows the library's last error in the drawer", async () => {
    load({ automationStates: [state({ lastError: "No indexer answered", nextSearchUtc: minutesFromNow(60) })] });
    openAt();
    fireEvent.click(screen.getByText("Films"));
    const drawer = within(await screen.findByRole("dialog"));
    const error = drawer.getByText("No indexer answered");
    expect(error.className).toContain("text-destructive");
    expect(drawer.getByText("next in 1 h")).toBeInTheDocument();
  });
});

describe("Automation overview Recent cycles", () => {
  it("each row reads its library and type, how the cycle started, what it queued and when it ran", () => {
    load({
      libraries: [library(), library({ id: "lib-2", name: "Shows", mediaType: "tv" })],
      searchCycles: [
        cycle({ libraryName: "Films", triggerKind: "manual", queuedCount: 2, plannedCount: 5, skippedCount: 1, startedUtc: minutesFromNow(-60), completedUtc: minutesFromNow(-59) }),
        cycle({ libraryName: "Shows", mediaType: "tv", triggerKind: "scheduled", status: "failed", startedUtc: minutesFromNow(-120), completedUtc: null }),
        cycle({ libraryName: "Docs", triggerKind: "upgrade", status: "running" }),
        cycle({ libraryName: "Misc", triggerKind: "retry-sweep" })
      ]
    });
    openAt();
    const rows = screen.getAllByRole("row").filter((candidate) => candidate.textContent?.includes("queued"));
    expect(rows).toHaveLength(4);
    expect(rows[0].textContent).toContain("FilmsMovies");
    expect(rows[0].textContent).toContain("Manual");
    expect(rows[0].textContent).toContain("2 queued");
    expect(rows[0].textContent).toContain("5 planned · 1 skipped");
    expect(rows[0].textContent).toContain("1 h ago");
    expect(rows[0].textContent).toContain("Took 1m");
    expect(rows[0].textContent).toContain("completed");
    expect(rows[1].textContent).toContain("ShowsTV shows");
    expect(rows[1].textContent).toContain("Scheduled");
    expect(rows[1].textContent).toContain("Still running");
    expect(rows[1].textContent).toContain("failed");
    expect(rows[2].textContent).toContain("Upgrade sweep");
    expect(rows[3].textContent).toContain("retry-sweep");
  });

  it("the heading counts 1 run, N runs, or Latest 12 of N runs, and 12 rows are shown", () => {
    load({ searchCycles: [cycle()] });
    const first = render(<MemoryRouter initialEntries={["/search-cycles"]}><SearchCyclesPage /></MemoryRouter>);
    expect(screen.getByText("1 run")).toBeInTheDocument();
    first.unmount();

    load({ searchCycles: [cycle(), cycle(), cycle()] });
    const second = render(<MemoryRouter initialEntries={["/search-cycles"]}><SearchCyclesPage /></MemoryRouter>);
    expect(screen.getByText("3 runs")).toBeInTheDocument();
    second.unmount();

    load({ searchCycles: Array.from({ length: 15 }, () => cycle()) });
    render(<MemoryRouter initialEntries={["/search-cycles"]}><SearchCyclesPage /></MemoryRouter>);
    expect(screen.getByText("Latest 12 of 15 runs")).toBeInTheDocument();
    expect(screen.getAllByRole("row").filter((candidate) => candidate.textContent?.includes("queued"))).toHaveLength(12);
  });

  it("the columns sort by library, trigger, result, ran and status", async () => {
    load({
      searchCycles: [
        cycle({ libraryName: "Bravo", triggerKind: "scheduled", queuedCount: 3, status: "failed", startedUtc: minutesFromNow(-30) }),
        cycle({ libraryName: "Alpha", triggerKind: "upgrade", queuedCount: 9, status: "completed", startedUtc: minutesFromNow(-90) }),
        cycle({ libraryName: "Charlie", triggerKind: "manual", queuedCount: 1, status: "running", startedUtc: minutesFromNow(-5) })
      ]
    });
    openAt();
    const order = () => screen.getAllByRole("row").filter((candidate) => candidate.textContent?.includes("queued")).map((candidate) => candidate.textContent!.match(/Alpha|Bravo|Charlie/)![0]);
    const sortBy = async (column: string) => {
      const header = screen.getAllByRole("columnheader").filter((candidate) => candidate.textContent === column).pop()!;
      await userEvent.click(within(header).getByRole("button"));
      return header;
    };
    expect(order()).toEqual(["Bravo", "Alpha", "Charlie"]);
    await sortBy("Library");
    expect(order()).toEqual(["Alpha", "Bravo", "Charlie"]);
    await sortBy("Trigger");
    expect(order()).toEqual(["Charlie", "Bravo", "Alpha"]); // Manual, Scheduled, Upgrade sweep
    await sortBy("Result");
    expect(order()).toEqual(["Charlie", "Bravo", "Alpha"]); // 1, 3, 9 queued
    await sortBy("Result");
    expect(order()).toEqual(["Alpha", "Bravo", "Charlie"]); // 9, 3, 1 queued
    await sortBy("Ran");
    expect(order()).toEqual(["Alpha", "Bravo", "Charlie"]); // oldest first
    await sortBy("Ran");
    expect(order()).toEqual(["Charlie", "Bravo", "Alpha"]); // newest first
    const status = await sortBy("Status");
    expect(status).toHaveAttribute("aria-sort", "ascending");
    expect(order()).toEqual(["Bravo", "Charlie", "Alpha"]); // failed, running, completed
  });

  it("with no cycle the card says No search cycles yet", () => {
    openAt();
    expect(screen.getByText("No search cycles yet")).toBeInTheDocument();
    expect(screen.getByText("Once a library runs a missing or upgrade search, each cycle and what it queued shows up here.")).toBeInTheDocument();
  });
});

describe("Automation overview Automation cell", () => {
  it("reads Paused in the warning tone with its help line while automation is paused, and Running without it", () => {
    load({ settings: { ...emptyPlatformSettingsSnapshot, autoStartJobs: false } });
    const paused = render(<MemoryRouter initialEntries={["/search-cycles"]}><SearchCyclesPage /></MemoryRouter>);
    const pausedCell = screen.getByText("Queued work is held safely").parentElement as HTMLElement;
    expect(within(pausedCell).getByText("Paused").className).toContain("text-warning");
    
    paused.unmount();
    load();
    render(<MemoryRouter initialEntries={["/search-cycles"]}><SearchCyclesPage /></MemoryRouter>);
    const runningCell = screen.getByText("Deluno searches on schedule").parentElement as HTMLElement;
    expect(within(runningCell).getByText("Running").className).not.toContain("text-warning");
    
  });
});

describe("Automation overview busy states", () => {
  it("a failed Pause automation request toasts the error and leaves the button as it was", async () => {
    authedFetchMock.mockImplementation((url: string) => String(url) === "/api/settings/automation" ? reply({}, false, 500) : reply([]));
    openAt();
    fireEvent.click(screen.getByRole("button", { name: "Pause automation" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not update automation."));
    expect(screen.getByRole("button", { name: "Pause automation" })).not.toBeDisabled();
    expect(screen.queryByRole("button", { name: "Resume automation" })).toBeNull();
  });

  it("the Pause automation button shows it is working and is disabled while the request is in flight", async () => {
    let finish: (value: Response) => void = () => undefined;
    authedFetchMock.mockImplementation((url: string) => String(url) === "/api/settings/automation" ? new Promise<Response>((resolve) => { finish = resolve; }) : reply([]));
    openAt();
    const button = screen.getByRole("button", { name: "Pause automation" });
    fireEvent.click(button);
    await waitFor(() => expect(button).toBeDisabled());
    expect(button.querySelector("svg.animate-spin")).not.toBeNull();
    finish(await reply({}));
    await waitFor(() => expect(button).not.toBeDisabled());
  });

  it("while Search now is in flight the drawer's Save schedule and Skip next cycle are held", async () => {
    let finish: (value: Response) => void = () => undefined;
    authedFetchMock.mockImplementation((url: string) => String(url) === "/api/libraries/lib-1/search-now" ? new Promise<Response>((resolve) => { finish = resolve; }) : reply([]));
    openAt();
    fireEvent.click(screen.getByText("Films"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.click(drawer.getByRole("button", { name: "Search now" }));
    await waitFor(() => expect(drawer.getByRole("button", { name: "Save schedule" })).toBeDisabled());
    expect(drawer.getByRole("button", { name: "Skip next cycle" })).toBeDisabled();
    finish(await reply({}));
    await waitFor(() => expect(drawer.getByRole("button", { name: "Skip next cycle" })).not.toBeDisabled());
  });
});
