import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../lib/route-cache";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { SystemPage, systemSources } from "./system-screen";

/**
 * The System > Health card, its suggestions, the summary tiles and the pushes that keep the screen current, row by row (#1329). The screen is handed its data the way the route cache hands it; every
 * request it makes goes through the mocked sign-in fetch, so each test shows what was asked and what was said.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: toastMock, Toaster: () => null }));

const navigateMock = vi.hoisted(() => vi.fn());
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => navigateMock
}));

const revalidateMock = vi.fn();
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const loaderData: any = {
  activity: [],
  activityDisplaySettings: { pageSize: 250 },
  automation: [],
  backupSettings: null,
  backups: [],
  downloadClients: [],
  healthChecks: { checks: [], summary: { problemCount: 0, unsnoozedProblemCount: 0 } },
  failedJobs: [],
  indexers: [],
  jobs: [],
  lanAccess: null,
  libraries: [],
  monitoring: { alerts: [] },
  processors: [],
  recycleBin: [],
  recycleBinSettings: null,
  retryWindows: [],
  searchCycles: [],
  settings: emptyPlatformSettingsSnapshot,
  systemInfo: null,
  systemTasks: []
};

const pushHandlers = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock("../lib/use-signalr", async (importOriginal) => {
  const original = await importOriginal<typeof import("../lib/use-signalr")>();
  return {
    ...original,
    useSignalREvent: (event: string, subjects: never, handler: (payload: unknown) => void) => {
      const set = pushHandlers.get(event) ?? new Set();
      set.add(handler);
      pushHandlers.set(event, set);
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (original.useSignalREvent as any)(event, subjects, handler);
    },
    // The live events (jobs, search progress, client state) are registered through the same door.
    useLiveEvent: (event: string, handler: (payload: unknown) => void) => {
      const set = pushHandlers.get(event) ?? new Set();
      set.add(handler);
      pushHandlers.set(event, set);
    }
  };
});

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(loaderData, revalidateMock),
    refetchSources: async () => { revalidateMock(); }
  };
});

function jsonResponse(body: unknown, ok = true, status = ok ? 200 : 500) {
  return Promise.resolve({
    ok,
    status,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(typeof body === "string" ? body : JSON.stringify(body))
  } as Response);
}

/** Answers every request that changes something with this; every read (the screen reads many lists) gets an empty list. */
function answerPost(body: unknown, ok = true, status = ok ? 200 : 500) {
  authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => (init?.method ? jsonResponse(body, ok, status) : jsonResponse([])));
}

const baseCheck = {
  category: "system", status: "problem", message: "Did not finish.", cause: null, fix: null, detailsJson: null,
  lastRunUtc: "2026-10-14T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null
};
const check = (over: Record<string, unknown>) => ({ ...baseCheck, ...over });

function setChecks(checks: unknown[], summary: Record<string, number> = { problemCount: 0, unsnoozedProblemCount: 0 }) {
  loaderData.healthChecks = { checks, summary };
}

function renderSystem(path = "/system") {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <SystemPage />
    </MemoryRouter>
  );
}

function resetAll() {
  authedFetchMock.mockReset();
  revalidateMock.mockReset();
  toastMock.success.mockReset();
  toastMock.error.mockReset();
  navigateMock.mockReset();
  pushHandlers.clear();
  loaderData.healthChecks = { checks: [], summary: { problemCount: 0, unsnoozedProblemCount: 0 } };
  loaderData.monitoring = { alerts: [] };
  loaderData.indexers = [];
  loaderData.downloadClients = [];
  loaderData.processors = [];
  loaderData.systemTasks = [];
  loaderData.automation = [];
  loaderData.searchCycles = [];
  loaderData.retryWindows = [];
  loaderData.lanAccess = null;
}

/** The text of the summary-strip tile with this label (its label, value and help). */
function tile(label: string): string {
  const labelSpan = screen.getAllByText(label, { selector: "span" })[0]!;
  return labelSpan.parentElement!.textContent ?? "";
}


const later = () => new Date(Date.now() + 3_600_000).toISOString();

describe("The Health card title, empty states and ordering", () => {
  afterEach(resetAll);

  it("says 'Everything checked out' with nothing to show, 'N need(s) attention' singular and plural, and 'All clear' when only passing checks remain", () => {
    renderSystem();
    expect(screen.getByText("Everything checked out")).toBeInTheDocument();
    expect(screen.getByText("No checks have run yet")).toBeInTheDocument();
    cleanup();

    setChecks([check({ id: "a", name: "One problem" })]);
    renderSystem();
    expect(screen.getByText("1 needs attention")).toBeInTheDocument();
    cleanup();

    setChecks([check({ id: "a", name: "One problem" }), check({ id: "b", name: "Another problem" })]);
    renderSystem();
    expect(screen.getByText("2 need attention")).toBeInTheDocument();
    cleanup();

    setChecks([check({ id: "c", name: "Fine", status: "ok", message: "Passing." })]);
    renderSystem();
    expect(screen.getByText("All clear")).toBeInTheDocument();
    expect(screen.queryByText("No checks have run yet")).not.toBeInTheDocument();
  });

  it("with only optional suggestions it says 'All clear' and that the suggestions are optional, not 'No checks have run yet'", () => {
    setChecks([check({ id: "security:allowed-hosts", name: "Allowed Hosts", status: "suggestion", message: "Deluno answers on any address." })]);
    renderSystem();

    expect(screen.getAllByText("All clear").length).toBeGreaterThan(0);
    expect(screen.getByText("Nothing needs a look. The suggestions below are optional.")).toBeInTheDocument();
    expect(screen.queryByText("No checks have run yet")).not.toBeInTheDocument();
  });

  it("lists problems first, then unknown, then the rest, and a snoozed problem stops counting", () => {
    setChecks([
      check({ id: "w", name: "W a warning", status: "warning", message: "Slow." }),
      check({ id: "o", name: "O fine", status: "ok", message: "Fine." }),
      check({ id: "u", name: "U not answered", status: "unknown", message: "No answer yet." }),
      check({ id: "p", name: "P a problem", message: "Broken." }),
      check({ id: "s", name: "S snoozed problem", message: "Broken too.", snoozedUntilUtc: later() })
    ]);
    renderSystem();

    const all = Array.from(document.body.querySelectorAll("*"));
    const at = (name: string) => all.indexOf(screen.getByText(name));
    expect(Math.max(at("P a problem"), at("S snoozed problem"))).toBeLessThan(at("U not answered"));
    expect(at("U not answered")).toBeLessThan(Math.min(at("W a warning"), at("O fine")));
    expect(screen.getByText("3 need attention")).toBeInTheDocument();
  });
});

describe("The status chip on a health row", () => {
  afterEach(resetAll);

  it("reads ok, problem, warning or unknown, and 'snoozed' for a problem while its snooze runs", () => {
    setChecks([
      check({ id: "a", name: "Check A", status: "ok", message: "Fine." }),
      check({ id: "b", name: "Check B", status: "problem", message: "Broken." }),
      check({ id: "c", name: "Check C", status: "warning", message: "Slow." }),
      check({ id: "d", name: "Check D", status: "unknown", message: "Waiting." }),
      check({ id: "e", name: "Check E", status: "problem", message: "Broken too.", snoozedUntilUtc: later() })
    ]);
    renderSystem();

    const rowOf = (name: string) => within(screen.getByText(name).closest("li, tr, [role='row'], [role='listitem']") as HTMLElement);
    expect(rowOf("Check A").getByText("ok")).toBeInTheDocument();
    expect(rowOf("Check B").getByText("problem")).toBeInTheDocument();
    expect(rowOf("Check C").getByText("warning")).toBeInTheDocument();
    expect(rowOf("Check D").getByText("unknown")).toBeInTheDocument();
    expect(rowOf("Check E").getByText("snoozed")).toBeInTheDocument();
    expect(rowOf("Check E").queryByText("problem")).not.toBeInTheDocument();
  });
});

describe("Dismiss and Show on a health row", () => {
  afterEach(resetAll);

  const issue = check({ id: "processor-output-issue:c1", category: "processor", name: "Arrival: missing from output", detailsJson: JSON.stringify({ recoveryCaseId: "c1", recoveryMediaType: "movies" }) });

  it("treats a 404 (already closed somewhere else) as success, and any other failure shows an error toast and leaves the row", async () => {
    setChecks([issue]);
    answerPost({}, false, 404);
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Dismiss: Arrival: missing from output" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Dismissed. Nothing was deleted."));
    expect(revalidateMock).toHaveBeenCalledTimes(1);
    expect(toastMock.error).not.toHaveBeenCalled();

    revalidateMock.mockReset();
    toastMock.success.mockReset();
    answerPost({}, false, 500);
    fireEvent.click(screen.getByRole("button", { name: "Dismiss: Arrival: missing from output" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("That could not be dismissed."));
    expect(toastMock.success).not.toHaveBeenCalled();
    expect(revalidateMock).not.toHaveBeenCalled();
    expect(screen.getByText("Arrival: missing from output")).toBeInTheDocument();
  });

  it("'Show' on a tag warning opens the screen its details link to, and a row with no safe link has no Show", () => {
    setChecks([
      check({ id: "tags:stranded", name: "Titles with a removed tag", detailsJson: JSON.stringify({ link: "/movies?tag=gone" }) }),
      check({ id: "tags:odd", name: "Tag with an outside link", detailsJson: JSON.stringify({ link: "https://elsewhere.example/x" }) })
    ]);
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Show: Titles with a removed tag" }));
    expect(navigateMock).toHaveBeenCalledWith("/movies?tag=gone");
    expect(screen.queryByRole("button", { name: "Show: Tag with an outside link" })).not.toBeInTheDocument();
  });
});

describe("The Suggestions card", () => {
  afterEach(resetAll);

  it("lists each optional setting that is off with its words, and its button opens the screen that turns it on", () => {
    setChecks([
      check({ id: "security:allowed-hosts", name: "Allowed Hosts", status: "suggestion", message: "Deluno answers on any address.", cause: "Allowed Hosts is off.", fix: "Tick the addresses to use." }),
      check({ id: "download-health:dangerous-file", name: "Dangerous files", status: "suggestion", message: "Dangerous files are not blocked.", cause: null, fix: null }),
      check({ id: "other:thing", name: "Something else", status: "suggestion", message: "An optional thing." })
    ]);
    renderSystem();

    expect(screen.getByText("Suggestions")).toBeInTheDocument();
    expect(screen.getByText("3 optional")).toBeInTheDocument();
    expect(screen.getAllByText("Switched off")).toHaveLength(3);
    expect(screen.getByText("Allowed Hosts is off. Tick the addresses to use.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Open Security: Allowed Hosts" }));
    expect(navigateMock).toHaveBeenLastCalledWith("/system/security");
    fireEvent.click(screen.getByRole("button", { name: "Turn it on: Dangerous files" }));
    expect(navigateMock).toHaveBeenLastCalledWith("/search-cycles/failed-downloads");
    expect(screen.queryByRole("button", { name: /: Something else/ })).not.toBeInTheDocument();
  });
});

describe("The summary tiles for media roots, monitoring alerts and running jobs", () => {
  afterEach(resetAll);

  const snapshot = (alerts: unknown[]) => ({
    generatedUtc: "2026-10-15T00:00:00Z",
    readiness: { totalChecks: 5, failedChecks: 1 },
    storage: { freePercent: 42.5 },
    services: { queuedJobs: 3 },
    performance: { apiLatency: { p95Ms: 120.4, errorRatePercent: 0.5 }, averageSearchCycleSeconds: 12, averageGrabToDetectionSeconds: 30, averageDetectionToImportSeconds: 45 },
    alerts,
    machine: null
  });
  const alert = (n: number) => ({ code: `alert-${n}`, severity: n === 1 ? "critical" : "warning", summary: `Summary ${n}`, details: `Details ${n}` });
  const job = (id: string, status: string, over: Record<string, unknown> = {}) => ({ id, jobType: "library.refresh", label: `Job ${id}`, status, source: "schedule", sourceLabel: "The schedule", attempts: 1, maxAttempts: 3, nextAttemptUtc: null, createdUtc: "2026-10-01T00:00:00Z", startedUtc: null, completedUtc: null, workerId: null, payloadJson: null, lastError: null, relatedEntityType: null, relatedEntityId: null, ...over });

  it("Media roots counts each distinct library root folder once, whatever the letter case or trailing slash", () => {
    loaderData.libraries = [
      { id: "1", name: "Films", rootPath: "D:/Media/Movies" },
      { id: "2", name: "Kids films", rootPath: "d:/media/movies/" },
      { id: "3", name: "Shows", rootPath: "D:/Media/TV" },
      { id: "4", name: "Unset", rootPath: "" }
    ];
    renderSystem();
    expect(tile("Media roots")).toContain("2");
    expect(tile("Media roots")).toContain("folders Deluno writes to");
    loaderData.libraries = [];
  });

  it("Monitoring alerts shows the count and 'nothing raised' when there are none", () => {
    renderSystem();
    expect(tile("Monitoring alerts")).toContain("0");
    expect(tile("Monitoring alerts")).toContain("nothing raised");
    cleanup();

    loaderData.monitoring = snapshot([alert(1), alert(2)]);
    renderSystem();
    expect(tile("Monitoring alerts")).toContain("2");
    expect(tile("Monitoring alerts")).toContain("raised by monitoring");
  });

  it("the Monitoring dashboard card shows checks passing, open alerts, API p95, queued jobs, storage free and the three averages", () => {
    loaderData.monitoring = snapshot([alert(1), alert(2)]);
    renderSystem();

    const card = screen.getByText("Monitoring dashboard").closest("section, div[class]")!.parentElement as HTMLElement;
    expect(within(card).getByText("4/5")).toBeInTheDocument();
    const metric = (label: string) => within(card).getByText(label).parentElement!.textContent ?? "";
    expect(metric("Open alerts")).toContain("2");
    expect(metric("API p95")).toContain("120ms");
    expect(metric("Queued jobs")).toContain("3");
    expect(within(card).getByText("Storage free: 42.5%")).toBeInTheDocument();
    expect(within(card).getByText("Search cycle avg: 12.0s")).toBeInTheDocument();
    expect(within(card).getByText("Grab to detect avg: 30.0s")).toBeInTheDocument();
    expect(within(card).getByText("Detect to import avg: 45.0s")).toBeInTheDocument();
  });

  it("Running jobs counts queued and working background jobs only, with its help saying 'queued or working' or 'nothing queued'", () => {
    loaderData.jobs = [job("1", "queued"), job("2", "running"), job("3", "completed"), job("4", "running", { relatedEntityType: "movie", relatedEntityId: "m1" })];
    renderSystem();
    expect(tile("Running jobs")).toContain("2");
    expect(tile("Running jobs")).toContain("queued or working");
    cleanup();

    loaderData.jobs = [job("3", "completed")];
    renderSystem();
    expect(tile("Running jobs")).toContain("0");
    expect(tile("Running jobs")).toContain("nothing queued");
    loaderData.jobs = [];
  });
});

describe("The open System screen follows the server (#1329)", () => {
  afterEach(() => {
    vi.useRealTimers();
    resetAll();
  });

  async function raise(event: string, payload: unknown = {}) {
    vi.useFakeTimers();
    authedFetchMock.mockImplementation(() => jsonResponse([]));
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    renderSystem();
    const handlers = [...(pushHandlers.get(event) ?? [])];
    expect(handlers.length, `nothing on System listens for ${event}`).toBeGreaterThan(0);
    for (const handler of handlers) handler(payload);
    await vi.advanceTimersByTimeAsync(1_000);
    const keys = invalidate.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown }).queryKey));
    invalidate.mockRestore();
    return keys;
  }
  const keyOf = (source: { key: readonly unknown[] }) => JSON.stringify(source.key);

  it("SettingsChanged re-reads the settings and the LAN access, and nothing else", async () => {
    const keys = await raise("SettingsChanged");
    expect(keys).toContain(keyOf(systemSources.settings));
    expect(keys).toContain(keyOf(systemSources.lanAccess));
    expect(keys).not.toContain(keyOf(systemSources.libraries));
  });

  it("LibraryChanged re-reads the libraries, which the Media roots tile counts, and nothing else", async () => {
    const keys = await raise("LibraryChanged");
    expect(keys).toContain(keyOf(systemSources.libraries));
    expect(keys).not.toContain(keyOf(systemSources.settings));
  });

  it("no timer polls the screen: opening it starts no repeating interval", () => {
    const spy = vi.spyOn(globalThis, "setInterval");
    authedFetchMock.mockImplementation(() => jsonResponse([]));
    renderSystem();
    const polling = spy.mock.calls.filter((call) => (call[1] as number) >= 1000);
    spy.mockRestore();
    expect(polling).toHaveLength(0);
  });
});

describe("The Providers card", () => {
  afterEach(resetAll);

  const source = (id: string, healthStatus: string) => ({ id, name: `Source ${id}`, protocol: "torznab", healthStatus, lastHealthMessage: null, lastHealthTestUtc: "2026-10-14T00:00:00Z", lastHealthLatencyMs: 90 });
  const downloader = (id: string, healthStatus: string) => ({ id, name: `Downloader ${id}`, protocol: "qbittorrent", healthStatus, lastHealthMessage: "Reported fine.", lastHealthTestUtc: null, lastHealthLatencyMs: null });

  it("says 'Nothing connected yet' with no search source, client or processor, and counts '0 of 0 responding'", () => {
    renderSystem();

    expect(screen.getByText("Nothing connected yet")).toBeInTheDocument();
    expect(screen.getByText("0 of 0 responding")).toBeInTheDocument();
  });

  it("counts a processor as a row but not in the 'N of M responding' figure", () => {
    loaderData.indexers = [source("1", "healthy")];
    loaderData.processors = [{ id: "p1", name: "Processor One", provider: "weir", isEnabled: true, healthStatus: "unhealthy" }];
    renderSystem();

    expect(screen.queryByText("Nothing connected yet")).not.toBeInTheDocument();
    expect(screen.getByText("1 of 1 responding")).toBeInTheDocument();
  });

  it("clicking a search source or a download client opens its health drawer, and Close puts it away; a processor row opens nothing", async () => {
    loaderData.indexers = [source("1", "healthy")];
    loaderData.downloadClients = [downloader("2", "unreachable")];
    loaderData.processors = [{ id: "p1", name: "Processor One", provider: "weir", isEnabled: true, healthStatus: "healthy" }];
    renderSystem();

    fireEvent.click(screen.getByText("Source 1"));
    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getByText("Search source · torznab")).toBeInTheDocument();
    expect(within(drawer).getByText("Health result")).toBeInTheDocument();
    expect(within(drawer).getByText("90 ms")).toBeInTheDocument();
    const closers = within(drawer).getAllByRole("button", { name: "Close" });
    fireEvent.click(closers[closers.length - 1]!);
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    fireEvent.click(screen.getByText("Downloader 2"));
    const second = await screen.findByRole("dialog");
    expect(within(second).getByText("Download client · qbittorrent")).toBeInTheDocument();
    expect(within(second).getByText("Not tested")).toBeInTheDocument();
    const again = within(second).getAllByRole("button", { name: "Close" });
    fireEvent.click(again[again.length - 1]!);
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    fireEvent.click(screen.getByText("Processor One"));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});

describe("The Search automation latest run panel", () => {
  afterEach(resetAll);

  const cycle = (over: Record<string, unknown> = {}) => ({
    id: "c1", libraryId: "lib-1", libraryName: "Films", mediaType: "movies", triggerKind: "manual", status: "completed",
    plannedCount: 12, queuedCount: 3, skippedCount: 9, notesJson: null, startedUtc: "2026-10-15T09:00:00Z", completedUtc: "2026-10-15T09:01:00Z", searchKind: "combined", ...over
  });

  it("shows the library, what started it, when, the status badge and the Checked, Sent and Retry counts of the newest run only", () => {
    loaderData.searchCycles = [cycle(), cycle({ id: "older", libraryName: "Shows", triggerKind: "schedule", status: "running" })];
    renderSystem();

    const card = within(screen.getByText("Search automation").closest("section, div")!.parentElement!);
    expect(card.getByText("Films")).toBeInTheDocument();
    expect(card.getByText(/^manual \//)).toBeInTheDocument();
    expect(card.getByText("completed")).toBeInTheDocument();
    expect(card.queryByText("running")).not.toBeInTheDocument();
    expect(card.queryByText("Shows")).not.toBeInTheDocument();
    const metric = (label: string) => card.getByText(label).parentElement!.textContent ?? "";
    expect(metric("Checked")).toContain("12");
    expect(metric("Sent")).toContain("3");
    expect(metric("Retry")).toContain("9");
  });

  it("shows a running run with the running badge, and says no cycle has run yet when there is none", () => {
    loaderData.searchCycles = [cycle({ status: "running" })];
    renderSystem();
    expect(within(screen.getByText("Search automation").closest("section, div")!.parentElement!).getByText(/running/)).toBeInTheDocument();
    cleanup();

    loaderData.searchCycles = [];
    renderSystem();
    expect(screen.getByText(/No search cycles have run yet. Once libraries are enabled/)).toBeInTheDocument();
    expect(screen.queryByText("Checked")).not.toBeInTheDocument();
  });
});

describe("The scheduled passes list", () => {
  afterEach(resetAll);

  const pass = (key: string, over: Record<string, unknown> = {}) => ({
    key, name: `Pass ${key}`, description: `What ${key} does`, intervalSeconds: 21600, isConfigurable: true, lastStartedUtc: "2026-10-15T08:00:00Z",
    lastCompletedUtc: "2026-10-15T08:00:02Z", lastResult: "completed", lastDurationMs: 1250, nextRunUtc: "2026-10-15T14:00:00Z", ...over
  });
  const rowOf = (name: string) => within(screen.getByText(name).closest("[role=row], li, tr") as HTMLElement);

  it("lists each pass with its name, what it does, its cadence, last run with how long it took, next run and result", () => {
    loaderData.systemTasks = [pass("a"), pass("b", { isConfigurable: false, intervalSeconds: 90, lastDurationMs: null })];
    renderSystem();

    const first = rowOf("Pass a");
    expect(first.getByText("What a does")).toBeInTheDocument();
    expect(first.getByText("6h · configured")).toBeInTheDocument();
    expect(first.getByText("1250ms")).toBeInTheDocument();
    expect(first.getByText("completed")).toBeInTheDocument();
    expect(first.queryByText("Not run yet")).not.toBeInTheDocument();
    expect(first.queryByText("Not scheduled")).not.toBeInTheDocument();

    const second = rowOf("Pass b");
    expect(second.getByText("90s")).toBeInTheDocument();
    expect(second.queryByText(/configured/)).not.toBeInTheDocument();
    expect(second.queryByText(/ms$/)).not.toBeInTheDocument();
  });

  it("says 'Not run yet' and 'Not scheduled' for a pass that has not run and has no next time", () => {
    loaderData.systemTasks = [pass("c", { lastCompletedUtc: null, lastDurationMs: null, nextRunUtc: null, lastResult: "idle" })];
    renderSystem();

    const row = rowOf("Pass c");
    expect(row.getByText("Not run yet")).toBeInTheDocument();
    expect(row.getByText("Not scheduled")).toBeInTheDocument();
  });

  it("only the file check and the leftover-downloads clean-up have a button; every other pass has none", () => {
    loaderData.systemTasks = [pass("library.file.check"), pass("download.leftover.cleanup"), pass("metadata.refresh"), pass("backup.create")];
    renderSystem();

    expect(rowOf("Pass library.file.check").getByRole("button", { name: "Check files now" })).toBeInTheDocument();
    expect(rowOf("Pass download.leftover.cleanup").getByRole("button", { name: "Run now" })).toBeInTheDocument();
    expect(rowOf("Pass metadata.refresh").queryByRole("button")).not.toBeInTheDocument();
    expect(rowOf("Pass backup.create").queryByRole("button")).not.toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /^(Check files now|Run now)$/ })).toHaveLength(2);
  });
});

describe("The This installation card's rows", () => {
  afterEach(() => {
    resetAll();
    loaderData.systemInfo = null;
    loaderData.settings = emptyPlatformSettingsSnapshot;
  });

  const info = (over: Record<string, unknown> = {}) => ({
    hostName: "MEDIA-BOX", uptimeSeconds: 3 * 86_400 + 4 * 3_600 + 120, processStartUtc: new Date(Date.now() - 12 * 60_000).toISOString(),
    osDescription: "Windows 11", runtimeVersion: ".NET 10.0.0", databaseType: "SQLite", databaseLocation: "D:/Deluno/data", appDataPath: "D:/Deluno", hostLooksLikeContainerId: false, ...over
  });
  const valueOf = (label: string) => screen.getByText(label, { selector: "*" }).closest("[role=row], li, tr")!.textContent ?? "";

  it("shows the uptime as '3d 4h', the start as '12 min ago', the OS, the runtime, the database and the app-data folder", () => {
    loaderData.systemInfo = info();
    renderSystem();

    expect(valueOf("Uptime")).toContain("3d 4h");
    expect(valueOf("Started")).toContain("12 min ago");
    expect(valueOf("OS")).toContain("Windows 11");
    expect(valueOf(".NET runtime")).toContain(".NET 10.0.0");
    expect(valueOf("Database")).toContain("SQLite · D:/Deluno/data");
    expect(valueOf("App-data folder")).toContain("D:/Deluno");
  });

  it("shows '0m' for a brand-new start, and 'Unknown' for every row it cannot read", () => {
    loaderData.systemInfo = info({ uptimeSeconds: 0 });
    renderSystem();
    expect(valueOf("Uptime")).toContain("0m");
    cleanup();

    loaderData.systemInfo = null;
    renderSystem();
    for (const label of ["Runs on", "Uptime", "Started", "OS", ".NET runtime", "Database", "App-data folder"]) expect(valueOf(label)).toContain("Unknown");
  });

  it("leads with the computer's name, shows the saved theme, and always says sign-in is Required", () => {
    loaderData.systemInfo = info();
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, uiTheme: "midnight", skipLocalAuth: true };
    renderSystem();

    expect(valueOf("Runs on")).toContain("MEDIA-BOX");
    expect(valueOf("Interface")).toContain("midnight");
    expect(valueOf("Sign-in")).toContain("Required");
    expect(valueOf("Sign-in")).not.toMatch(/skip|local/i);
  });

});
