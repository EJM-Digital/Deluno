import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../lib/route-cache";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { SystemPage, systemSources } from "./system-screen";

/**
 * The System > Health screen, row by row (#1294): the summary strip, each health row's buttons, the
 * provider list, the system tasks, the metadata card, the search automation card, the monitoring card and the
 * pushes that keep them current. The screen is handed its data the way the route cache hands it; every
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

const indexer = (id: string, healthStatus: string) => ({ id, name: `Indexer ${id}`, protocol: "torznab", healthStatus });
const client = (id: string, healthStatus: string) => ({ id, name: `Client ${id}`, protocol: "torrent", healthStatus });

describe("The summary strip on System > Health", () => {
  afterEach(resetAll);

  it("Search sources tile shows healthy over total and turns to a warning when one is not responding", () => {
    loaderData.indexers = [indexer("1", "healthy"), indexer("2", "healthy"), indexer("3", "unhealthy")];
    renderSystem();
    expect(tile("Search sources")).toContain("2/3");
    expect(tile("Search sources")).toContain("one is not responding");
  });

  it("Search sources tile reads all responding when every source answers", () => {
    loaderData.indexers = [indexer("1", "healthy"), indexer("2", "healthy")];
    renderSystem();
    expect(tile("Search sources")).toContain("2/2");
    expect(tile("Search sources")).toContain("all responding");
  });

  it("Download clients tile shows healthy over total and turns to a warning when one is not responding", () => {
    loaderData.downloadClients = [client("1", "healthy"), client("2", "unreachable")];
    renderSystem();
    expect(tile("Download clients")).toContain("1/2");
    expect(tile("Download clients")).toContain("one is not responding");
  });

  it("Health checks tile shows the same count as the Health card title, leaving out snoozed problems and suggestions", () => {
    const later = new Date(Date.now() + 3_600_000).toISOString();
    setChecks(
      [
        check({ id: "a", name: "A real problem" }),
        check({ id: "b", name: "A snoozed problem", snoozedUntilUtc: later }),
        check({ id: "c", name: "Worth turning on", status: "suggestion" }),
        check({ id: "d", name: "Passing", status: "ok", message: "Fine." })
      ],
      { problemCount: 2, unsnoozedProblemCount: 1 }
    );
    renderSystem();
    expect(screen.getByText("1 needs attention")).toBeInTheDocument();
    expect(tile("Health checks")).toContain("1");
    expect(tile("Health checks")).toContain("need attention");
  });
});

describe("Each health row", () => {
  afterEach(resetAll);

  it("shows its name, its area, its message, and for a problem the cause and the fix in plain words", () => {
    setChecks([
      check({ id: "indexer:1", category: "indexer", name: "Indexer Alpha", message: "It did not answer.", cause: "The site is down.", fix: "Try again later." }),
      check({ id: "ok:1", category: "library", name: "Library Beta", status: "ok", message: "All folders exist.", cause: "never shown", fix: "never shown" })
    ]);
    renderSystem();
    expect(screen.getByText("Indexer Alpha")).toBeInTheDocument();
    expect(screen.getByText("It did not answer.")).toBeInTheDocument();
    expect(screen.getByText("The site is down. Try again later.")).toBeInTheDocument();
    expect(screen.getByText("Library Beta")).toBeInTheDocument();
    expect(screen.getByText("All folders exist.")).toBeInTheDocument();
    // A passing row says nothing of cause or fix.
    expect(screen.queryByText(/never shown/)).toBeNull();
  });

  it("Snooze 24h posts the snooze for 24 hours and reads the list again", async () => {
    setChecks([check({ id: "indexer:1", name: "Indexer Alpha" })], { problemCount: 1, unsnoozedProblemCount: 1 });
    answerPost({});
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Snooze 24h" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/indexer%3A1/snooze",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ hours: 24 }) })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("a snoozed problem reads snoozed, does not count, and Unsnooze posts the unsnooze", async () => {
    const later = new Date(Date.now() + 3_600_000).toISOString();
    setChecks([check({ id: "indexer:1", name: "Indexer Alpha", snoozedUntilUtc: later })], { problemCount: 1, unsnoozedProblemCount: 0 });
    answerPost({});
    renderSystem();

    expect(screen.getByText("snoozed")).toBeInTheDocument();
    expect(screen.getByText("All clear")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Snooze 24h" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Unsnooze" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/indexer%3A1/unsnooze", expect.objectContaining({ method: "POST" }))
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("Check now runs every health check again and says so; the rows are left to follow the server's push", async () => {
    answerPost({});
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Run all the health checks now" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/run", expect.objectContaining({ method: "POST" })));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Checking everything again. Rows that now pass will clear in a moment."));
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it("Check now says so in an error toast when the server refuses, and when the request fails", async () => {
    answerPost({}, false);
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Run all the health checks now" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Deluno could not start the checks. Try again."));

    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => (init?.method ? Promise.reject(new Error("offline")) : jsonResponse([])));
    fireEvent.click(screen.getByRole("button", { name: "Run all the health checks now" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledTimes(2));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  const startupRow = () => check({ id: "system:background-problem:startup:collection-exclusions", name: "Collection exclusions were not moved" });

  it("Try again on a start-up step shows what worked and refreshes when it cleared", async () => {
    setChecks([startupRow()]);
    answerPost({ cleared: true, message: "It worked, so the note is gone." });
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Try again: Collection exclusions were not moved" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("It worked, so the note is gone."));
    expect(toastMock.error).not.toHaveBeenCalled();
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("Try again on a start-up step says it still did not work when the step fails again", async () => {
    setChecks([startupRow()]);
    answerPost({ cleared: false, message: "It still did not work." });
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Try again: Collection exclusions were not moved" }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("It still did not work."));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("Try again on a start-up step says to check the logs when the server refuses", async () => {
    setChecks([startupRow()]);
    answerPost({}, false, 404);
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Try again: Collection exclusions were not moved" }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Deluno could not try that again. Check its logs."));
  });

  it("Clear on a start-up step deletes it by its start-up key and refreshes; a job row only snoozes", async () => {
    setChecks([startupRow(), check({ id: "system:background-problem:lane:import", name: "Import lane keeps failing" })]);
    answerPost({});
    renderSystem();

    expect(screen.getAllByRole("button", { name: /^Clear:/ })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Clear: Collection exclusions were not moved" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/background-problems/startup%3Acollection-exclusions",
        expect.objectContaining({ method: "DELETE" })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
    expect(screen.getAllByRole("button", { name: "Snooze 24h" })).toHaveLength(1);
  });

  it("a one-time note row shows Clear and an ordinary row does not; Clear deletes the note and refreshes", async () => {
    setChecks([
      check({ id: "notice:download-folders", category: "notice", name: "Downloads now go to the downloads folder" }),
      check({ id: "indexer:1", category: "indexer", name: "An indexer" })
    ]);
    answerPost({});
    renderSystem();

    expect(screen.getAllByRole("button", { name: /^Clear:/ })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Clear: Downloads now go to the downloads folder" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith("/api/system/health/notices/notice%3Adownload-folders", expect.objectContaining({ method: "DELETE" }))
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("Dismiss on a series import issue posts to the series dismiss route, says nothing was deleted, and refreshes", async () => {
    setChecks([
      check({
        id: "processor-output-issue:c9", category: "processor", name: "Tales: missing from a processor's output",
        detailsJson: JSON.stringify({ recoveryCaseId: "c9", recoveryMediaType: "series" })
      })
    ]);
    answerPost({});
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Dismiss: Tales: missing from a processor's output" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/series/import-recovery/c9/dismiss", expect.objectContaining({ method: "POST" })));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Dismissed. Nothing was deleted."));
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("a suggestion is optional: it is listed apart and never counts as needing attention", () => {
    setChecks([check({ id: "suggest:1", name: "Turn on stall handling", status: "suggestion", message: "It is off." })], { problemCount: 0, unsnoozedProblemCount: 0 });
    renderSystem();
    expect(screen.getAllByText("All clear").length).toBeGreaterThan(0);
    expect(screen.getByText("Suggestions")).toBeInTheDocument();
    expect(tile("Health checks")).toContain("all checks pass");
  });
});

describe("Processor-output rows act on the stray item", () => {
  afterEach(resetAll);

  const outputRow = (details: Record<string, unknown>) =>
    check({ id: "processor-output:abc", category: "processor", name: "Stray item in the output", detailsJson: JSON.stringify({ libraryId: "lib-1", ...details }) });

  it("Import appears for a media file only and opens manual import with the path and the media type filled in", () => {
    setChecks([
      outputRow({ path: "D:/out/Arrival.mkv", kind: "file", isMedia: true }),
      check({ id: "processor-output:scraps", category: "processor", name: "Scraps", detailsJson: JSON.stringify({ libraryId: "lib-1", path: "D:/out/readme.nfo", kind: "scraps", isMedia: false }) })
    ]);
    renderSystem();

    expect(screen.getAllByRole("button", { name: "Import" })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Import" }));

    expect(navigateMock).toHaveBeenCalledTimes(1);
    const target = new URL(navigateMock.mock.calls[0]![0] as string, "http://deluno.test");
    expect(target.pathname).toBe("/queue");
    expect(target.searchParams.get("manualImportPath")).toBe("D:/out/Arrival.mkv");
    expect(target.searchParams.get("manualImportMediaType")).toBe("movies");
  });

  it("Import on a folder opens manual import as TV", () => {
    setChecks([outputRow({ path: "D:/out/Tales.S01", kind: "folder", isMedia: true })]);
    renderSystem();
    fireEvent.click(screen.getByRole("button", { name: "Import" }));
    expect(new URL(navigateMock.mock.calls[0]![0] as string, "http://deluno.test").searchParams.get("manualImportMediaType")).toBe("tv");
  });

  it("Clear posts the item's library and path to the delete route and refreshes", async () => {
    setChecks([outputRow({ path: "D:/out/Arrival.mkv", kind: "file", isMedia: true })]);
    answerPost({});
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Clear Arrival.mkv" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/integrations/processors/output/delete",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ libraryId: "lib-1", path: "D:/out/Arrival.mkv" }) })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("a failed Clear leaves the row where it is and does not refresh", async () => {
    setChecks([outputRow({ path: "D:/out/Arrival.mkv", kind: "file", isMedia: true })]);
    answerPost({}, false, 409);
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Clear Arrival.mkv" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByRole("button", { name: "Clear Arrival.mkv" })).not.toBeDisabled());
    expect(revalidateMock).not.toHaveBeenCalled();
    expect(screen.getByText("Stray item in the output")).toBeInTheDocument();
  });

  it("a spare copy of a film offers Keep for 30 days and Remove, and neither is an Import", async () => {
    setChecks([outputRow({ path: "D:/out/Arrival.2.mkv", kind: "file", isMedia: true, spareCopyOf: "Arrival (2016)" })]);
    answerPost({});
    renderSystem();

    expect(screen.queryByRole("button", { name: "Import" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Keep the spare copy of Arrival (2016)" }));
    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/processor-output%3Aabc/snooze",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ hours: 720 }) })
      )
    );
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/integrations/processors/output/delete", expect.anything());

    fireEvent.click(screen.getByRole("button", { name: "Remove the spare copy of Arrival (2016)" }));
    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/integrations/processors/output/delete",
        expect.objectContaining({ body: JSON.stringify({ libraryId: "lib-1", path: "D:/out/Arrival.2.mkv" }) })
      )
    );
  });
});

describe("Providers list", () => {
  afterEach(resetAll);

  it("shows every search source, download client and processor with its name, kind and a status chip", () => {
    loaderData.indexers = [indexer("1", "healthy")];
    loaderData.downloadClients = [client("2", "unhealthy")];
    loaderData.processors = [
      { id: "3", name: "Processor Three", provider: "weir", isEnabled: true, healthStatus: "healthy" },
      { id: "4", name: "Processor Four", provider: "weir", isEnabled: false, healthStatus: "healthy" }
    ];
    renderSystem();

    const row = (name: string) => screen.getByText(name).closest("[role=row], tr, li, div[class*=grid]")!.parentElement!.textContent ?? "";
    expect(row("Indexer 1")).toContain("Search source");
    expect(row("Indexer 1")).toContain("torznab");
    expect(row("Indexer 1")).toContain("healthy");
    expect(row("Client 2")).toContain("Download client");
    expect(row("Client 2")).toContain("unhealthy");
    expect(row("Processor Three")).toContain("Processor");
    expect(row("Processor Four")).toContain("off");
    expect(screen.getByText("1 of 2 responding")).toBeInTheDocument();
  });

  it("a search source row shows its health, and a new source that is not healthy turns the count down", () => {
    loaderData.indexers = [indexer("1", "unhealthy")];
    renderSystem();
    const providers = screen.getByText("Providers").closest("section, div")!.parentElement!;
    expect(within(providers).getByText("Indexer 1")).toBeInTheDocument();
    expect(within(providers).getByText("unhealthy")).toBeInTheDocument();
    expect(within(providers).getByText("0 of 1 responding")).toBeInTheDocument();
  });
});

describe("System tasks", () => {
  afterEach(resetAll);

  const task = (key: string, name: string) => ({
    key, name, description: `${name} description`, intervalSeconds: 3600, isConfigurable: false, lastStartedUtc: null,
    lastCompletedUtc: null, lastResult: "ok", lastDurationMs: null, nextRunUtc: null
  });

  it("Check files now posts to the file check and says every file is still on disk when none is missing", async () => {
    loaderData.systemTasks = [task("library.file.check", "Check library files")];
    answerPost({ markedMissing: 0, unreachableRoots: 0 });
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Check files now" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/filesystem/file-check", expect.objectContaining({ method: "POST" })));
    expect(await screen.findByText("Every file Deluno is holding is still on disk.")).toBeInTheDocument();
  });

  it.each([
    [{ markedMissing: 1, unreachableRoots: 0 }, "1 title is now missing — search for it when you want it back."],
    [{ markedMissing: 4, unreachableRoots: 0 }, "4 titles are now missing — search for them when you want them back."],
    [{ markedMissing: 0, unreachableRoots: 1 }, "1 library root is unreachable, and nothing in it was touched."],
    [{ markedMissing: 2, unreachableRoots: 2 }, "2 titles are now missing — search for them when you want them back. 2 library roots are unreachable, and nothing in them was touched."]
  ])("Check files now says how many titles are missing and roots unreachable: %j", async (answer, sentence) => {
    loaderData.systemTasks = [task("library.file.check", "Check library files")];
    answerPost(answer);
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Check files now" }));

    expect(await screen.findByText(sentence)).toBeInTheDocument();
  });

  it.each([
    [{ removed: 0, held: 0 }, "No leftovers to clean up right now."],
    [{ removed: 1, held: 0 }, "1 leftover removed."],
    [{ removed: 3, held: 0 }, "3 leftovers removed."],
    [{ removed: 0, held: 2 }, "2 held back — still in use, too young, or unproven."],
    [{ removed: 2, held: 1 }, "2 leftovers removed. 1 held back — still in use, too young, or unproven."]
  ])("Run now on the leftover downloads task posts to the cleanup and says how many were removed or held back: %j", async (answer, sentence) => {
    loaderData.systemTasks = [task("download.leftover.cleanup", "Clean up leftover downloads")];
    answerPost(answer);
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Run now" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-clients/leftovers/cleanup", expect.objectContaining({ method: "POST" })));
    expect(await screen.findByText(sentence)).toBeInTheDocument();
  });

  it("a failed run says it did not finish and changed nothing, and every run button is off while one runs", async () => {
    loaderData.systemTasks = [task("library.file.check", "Check library files"), task("download.leftover.cleanup", "Clean up leftover downloads")];
    let reject: (reason: Error) => void = () => undefined;
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => (init?.method ? new Promise((_resolve, rejectIt) => { reject = rejectIt; }) : jsonResponse([])));
    renderSystem();

    fireEvent.click(screen.getByRole("button", { name: "Check files now" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Run now" })).toBeDisabled());
    expect(screen.getByRole("button", { name: "Checking…" })).toBeDisabled();

    reject(new Error("boom"));
    expect(await screen.findByText("That did not finish. Nothing was changed.")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Run now" })).not.toBeDisabled());
  });
});

describe("Metadata maintenance card", () => {
  afterEach(resetAll);

  const answerMetadata = (jobs: (path: string, body: unknown) => unknown, test?: unknown) =>
    authedFetchMock.mockImplementation((url: string, init?: RequestInit) => {
      if (url === "/api/metadata/status") return jsonResponse({ isConfigured: true, provider: "tmdb", message: "ok" });
      if (url === "/api/metadata/test") return jsonResponse(test ?? { isConfigured: true, resultCount: 1, message: "Found The Matrix (1999)." });
      if (url.endsWith("/metadata/jobs")) return jsonResponse(jobs(url, JSON.parse(String(init?.body))));
      return jsonResponse([]);
    });

  it("Check now posts a real lookup for The Matrix (1999) and reads the status again", async () => {
    answerMetadata(() => ({}));
    renderSystem();
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/metadata/status", expect.anything()));
    const statusReads = () => authedFetchMock.mock.calls.filter((call) => call[0] === "/api/metadata/status").length;
    const before = statusReads();

    fireEvent.click(screen.getByRole("button", { name: "Check now" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/metadata/test",
        expect.objectContaining({ method: "POST", body: JSON.stringify({ query: "The Matrix", mediaType: "movies", year: 1999 }) })
      )
    );
    await waitFor(() => expect(statusReads()).toBeGreaterThan(before));
  });

  async function runJob(name: string) {
    renderSystem();
    const row = screen.getByText(name).closest("[role=row]") as HTMLElement;
    fireEvent.click(within(row).getByRole("button", { name: "Run" }));
  }

  it("Fill in missing details queues the missing-only refresh for Movies and TV together and sums the answer", async () => {
    answerMetadata((url) => (url.includes("/movies/") ? { enqueuedCount: 3, remainingCount: 0, message: "m" } : { enqueuedCount: 2, remainingCount: 4, message: "s" }));
    await runJob("Fill in missing details");

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/metadata/jobs", expect.objectContaining({ method: "POST", body: JSON.stringify({ forceAll: false, take: 500 }) })));
    expect(authedFetchMock).toHaveBeenCalledWith("/api/series/metadata/jobs", expect.objectContaining({ method: "POST", body: JSON.stringify({ forceAll: false, take: 500 }) }));
    expect(await screen.findByText("Queued 5 titles. Another 4 still to go — Deluno keeps working through them in the background.")).toBeInTheDocument();
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("Fill in missing details says so when nothing needs refreshing", async () => {
    answerMetadata(() => ({ enqueuedCount: 0, remainingCount: 0, message: "none" }));
    await runJob("Fill in missing details");
    expect(await screen.findByText("Nothing needs refreshing.")).toBeInTheDocument();
  });

  it("Refresh all Movies queues a full refresh of movies only", async () => {
    answerMetadata(() => ({ enqueuedCount: 7, remainingCount: 0, message: "Queued 7 movies." }));
    await runJob("Refresh all Movies");

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/metadata/jobs", expect.objectContaining({ body: JSON.stringify({ forceAll: true, take: 500 }) })));
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/series/metadata/jobs", expect.anything());
    expect(await screen.findByText("Queued 7 movies.")).toBeInTheDocument();
  });

  it("Refresh all TV shows queues a full refresh of TV only", async () => {
    answerMetadata(() => ({ enqueuedCount: 2, remainingCount: 0, message: "Queued 2 shows." }));
    await runJob("Refresh all TV shows");

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/series/metadata/jobs", expect.objectContaining({ body: JSON.stringify({ forceAll: true, take: 500 }) })));
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/movies/metadata/jobs", expect.anything());
    expect(await screen.findByText("Queued 2 shows.")).toBeInTheDocument();
  });
});

describe("Search automation card", () => {
  afterEach(resetAll);

  const automationRow = (n: number) => ({
    libraryId: `lib-${n}`, libraryName: `Library ${n}`, status: "idle", searchRequested: false, nextSearchUtc: "2026-10-15T10:00:00Z"
  });

  it("lists the first five libraries with their status, next search and a Run now button", () => {
    loaderData.automation = [1, 2, 3, 4, 5, 6, 7].map(automationRow);
    renderSystem();
    for (const n of [1, 2, 3, 4, 5]) expect(screen.getByText(`Library ${n}`)).toBeInTheDocument();
    expect(screen.queryByText("Library 6")).toBeNull();
    expect(screen.getAllByText(/^idle \/ next /)).toHaveLength(5);
    const card = screen.getByText("Search automation").closest("section, div")!.parentElement!;
    expect(within(card).getAllByRole("button", { name: "Run now" })).toHaveLength(5);
  });

  const cycle = (overrides: Record<string, unknown> = {}) => ({
    id: "c1", libraryId: "lib-1", libraryName: "Films", mediaType: "movies", triggerKind: "schedule", status: "completed",
    plannedCount: 12, queuedCount: 3, skippedCount: 9, notesJson: null, startedUtc: "2026-10-15T09:00:00Z", completedUtc: "2026-10-15T09:01:00Z", searchKind: "combined", ...overrides
  });

  it("counts the libraries working now, the runs it remembers and the searches still waiting, and says so for a quiet instance", () => {
    loaderData.automation = [
      { ...automationRow(1), status: "running" }, { ...automationRow(2), searchRequested: true }, automationRow(3)
    ];
    loaderData.searchCycles = [cycle(), cycle({ id: "c2" })];
    loaderData.retryWindows = [
      { entityType: "movie", entityId: "a", libraryId: "lib-1", mediaType: "movies", actionKind: "missing", nextEligibleUtc: "2099-01-01T00:00:00Z", lastAttemptUtc: "2026-10-15T08:00:00Z", attemptCount: 1, lastResult: "Nothing found.", updatedUtc: "2026-10-15T08:00:00Z" },
      { entityType: "movie", entityId: "b", libraryId: "lib-1", mediaType: "movies", actionKind: "upgrade", nextEligibleUtc: "2000-01-01T00:00:00Z", lastAttemptUtc: "2026-10-15T08:00:00Z", attemptCount: 1, lastResult: null, updatedUtc: "2026-10-15T08:00:00Z" }
    ];
    renderSystem();

    const card = within(screen.getByText("Search automation").closest("section, div")!.parentElement!);
    const metric = (label: string) => card.getAllByText(label)[0].parentElement!;
    expect(within(metric("Active")).getByText("2")).toBeInTheDocument();
    expect(within(metric("Runs")).getByText("2")).toBeInTheDocument();
    expect(within(metric("Waiting")).getByText("1")).toBeInTheDocument();
  });

  it("shows the latest cycle with what it checked, sent and left to retry", () => {
    loaderData.searchCycles = [cycle(), cycle({ id: "older", libraryName: "Shows", plannedCount: 1, queuedCount: 1, skippedCount: 0 })];
    renderSystem();

    const card = within(screen.getByText("Search automation").closest("section, div")!.parentElement!);
    expect(card.getByText("Films")).toBeInTheDocument();
    expect(card.getByText(/^schedule \//)).toBeInTheDocument();
    const metric = (label: string) => card.getByText(label).parentElement!;
    expect(within(metric("Checked")).getByText("12")).toBeInTheDocument();
    expect(within(metric("Sent")).getByText("3")).toBeInTheDocument();
    expect(within(metric("Retry")).getByText("9")).toBeInTheDocument();
    expect(card.queryByText("Shows")).toBeNull();
  });

  it("says no cycle has run yet when none has", () => {
    renderSystem();
    expect(screen.getByText(/No search cycles have run yet/)).toBeInTheDocument();
  });

  it("lists the first four searches held back with what was found last and when they can run again", () => {
    loaderData.retryWindows = [1, 2, 3, 4, 5].map((n) => ({
      entityType: "movie", entityId: `film-${n}`, libraryId: "lib-1", mediaType: n === 1 ? "tv" : "movies", actionKind: n === 1 ? "upgrade" : "missing",
      nextEligibleUtc: "2099-01-01T00:00:00Z", lastAttemptUtc: "2026-10-15T08:00:00Z", attemptCount: 1, lastResult: n === 1 ? "Nothing better found." : null, updatedUtc: "2026-10-15T08:00:00Z"
    }));
    renderSystem();

    const card = within(screen.getByText("Search automation").closest("section, div")!.parentElement!);
    expect(card.getByText("Searches held back until")).toBeInTheDocument();
    expect(card.getByText("tv / upgrade")).toBeInTheDocument();
    expect(card.getByText("Nothing better found.")).toBeInTheDocument();
    expect(card.getAllByText("movies / missing")).toHaveLength(3);
    expect(card.getAllByText("Last search recorded")).toHaveLength(3);
  });

  it("Run now asks for a search of that library and reads the card again", async () => {
    loaderData.automation = [automationRow(1)];
    answerPost({});
    renderSystem();

    const card = screen.getByText("Search automation").closest("section, div")!.parentElement!;
    fireEvent.click(within(card).getByRole("button", { name: "Run now" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/libraries/lib-1/search-now", expect.objectContaining({ method: "POST" })));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  // #1332: a refusal used to throw out of AutomationCard.runNow uncaught and the person saw nothing; it now shows the server's reason.
  it("a refused search-now shows its message to the person", async () => {
    loaderData.automation = [automationRow(1)];
    answerPost("That library is paused.", false, 409);
    renderSystem();

    const card = screen.getByText("Search automation").closest("section, div")!.parentElement!;
    fireEvent.click(within(card).getByRole("button", { name: "Run now" }));

    expect(await screen.findByText(/That library is paused/)).toBeInTheDocument();
  });
});

describe("Monitoring card", () => {
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

  it("lists up to three alerts, each with its severity, summary and details, and the readiness figures", () => {
    loaderData.monitoring = snapshot([alert(1), alert(2), alert(3), alert(4)]);
    renderSystem();
    for (const n of [1, 2, 3]) {
      expect(screen.getByText(`Summary ${n}`)).toBeInTheDocument();
      expect(screen.getByText(`Details ${n}`)).toBeInTheDocument();
    }
    expect(screen.getByText("critical")).toBeInTheDocument();
    expect(screen.queryByText("Summary 4")).toBeNull();
    expect(screen.getByText("4/5")).toBeInTheDocument();
    expect(screen.getByText("120ms")).toBeInTheDocument();
    expect(screen.getByText("Storage free: 42.5%")).toBeInTheDocument();
    expect(tile("Monitoring alerts")).toContain("4");
  });

  it("links the two export endpoints for outside dashboards", () => {
    loaderData.monitoring = snapshot([alert(1)]);
    renderSystem();
    expect(screen.getByRole("link", { name: "Open Prometheus metrics export" })).toHaveAttribute("href", "/api/monitoring/export/prometheus");
    expect(screen.getByRole("link", { name: "Open Influx line-protocol export" })).toHaveAttribute("href", "/api/monitoring/export/influx");
  });

  it("is left off the screen when nothing is raised", () => {
    loaderData.monitoring = snapshot([]);
    renderSystem();
    expect(screen.queryByText("Monitoring dashboard")).toBeNull();
    expect(tile("Monitoring alerts")).toContain("nothing raised");
  });
});

describe("This installation card", () => {
  afterEach(resetAll);

  const lan = (over: Record<string, unknown>) => ({ state: "open", port: 5099, ruleName: "x", message: "m", canAllow: false, ...over });

  it.each([
    [lan({ state: "open", port: 5099 }), "5099", "Can connect"],
    [lan({ state: "blocked", port: 6000 }), "6000", "Cannot connect"],
    [null, "Unknown", "Unknown from here"]
  ])("shows the port and whether other devices can connect for %j", (status, port, reach) => {
    loaderData.lanAccess = status;
    renderSystem();
    const rowText = (label: string) => screen.getByText(label, { selector: "*" }).closest("div[class*=grid], li, tr")?.parentElement?.textContent ?? "";
    expect(rowText("Port")).toContain(port);
    expect(rowText("Other devices on your network")).toContain(reach);
  });
});

describe("The open System screen follows the server", () => {
  afterEach(() => {
    vi.useRealTimers();
    resetAll();
  });

  /** Raises one event the way the hub does and returns the cache keys the screen read again. */
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

  it("MonitoringChanged re-reads monitoring", async () => {
    const keys = await raise("MonitoringChanged");
    expect(keys).toContain(keyOf(systemSources.monitoring));
  });

  it("IndexerChanged re-reads the search sources", async () => {
    const keys = await raise("IndexerChanged");
    expect(keys).toContain(keyOf(systemSources.indexers));
  });

  it("DownloadClientChanged re-reads the download clients", async () => {
    const keys = await raise("DownloadClientChanged");
    expect(keys).toContain(keyOf(systemSources.downloadClients));
  });

  it("ClientStateChanged re-reads the download clients", async () => {
    const keys = await raise("ClientStateChanged");
    expect(keys).toContain(keyOf(systemSources.downloadClients));
  });

  it("AutomationStateChanged re-reads the search automation card", async () => {
    const keys = await raise("AutomationStateChanged");
    expect(keys).toContain(keyOf(systemSources.automation));
    expect(keys).toContain(keyOf(systemSources.retryWindows));
  });

  it("SearchRunCompleted re-reads the search runs and the held-back searches", async () => {
    const keys = await raise("SearchRunCompleted");
    expect(keys).toContain(keyOf(systemSources.searchCycles));
    expect(keys).toContain(keyOf(systemSources.retryWindows));
  });

  it("SearchCycleProgress re-reads the search runs and the automation state", async () => {
    const keys = await raise("SearchCycleProgress");
    expect(keys).toContain(keyOf(systemSources.searchCycles));
    expect(keys).toContain(keyOf(systemSources.automation));
  });

  it("JobChanged re-reads the jobs and the failed jobs", async () => {
    const keys = await raise("JobChanged");
    expect(keys).toContain(keyOf(systemSources.jobs));
    expect(keys).toContain(keyOf(systemSources.failedJobs));
  });
});
