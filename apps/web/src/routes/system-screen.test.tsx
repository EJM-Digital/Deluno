import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, RouterProvider, createMemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../lib/route-cache";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { outputIssueOf, SystemPage } from "./system-screen";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: toastMock, Toaster: () => null }));

const revalidateMock = vi.fn();
const loaderData = {
  activity: [],
  activityDisplaySettings: { pageSize: 250 },
  automation: [],
  backupSettings: null,
  backups: [],
  downloadClients: [],
  healthChecks: { checks: [], summary: { problemCount: 0 } },
  failedJobs: [],
  indexers: [],
  jobs: [],
  lanAccess: null,
  libraries: [],
  monitoring: null,
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
    }
  };
});

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => vi.fn()
}));

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(loaderData, revalidateMock),
    refetchSources: async () => { revalidateMock(); }
  };
});

function jsonResponse(body: unknown, ok = true) {
  return Promise.resolve({
    ok,
    status: ok ? 200 : 500,
    json: () => Promise.resolve(body)
  } as Response);
}

/**
 * #871: the Activity timeline's page-size control actually renders, and
 * changing it saves the new value and re-fetches with it -- not just that
 * the source text mentions the right identifiers.
 */
describe("Activity timeline page size (#871)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
  });

  it("renders the page-size card showing the loaded setting", () => {
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getByRole("combobox", { name: "Rows shown" })).toHaveTextContent("250 rows");
  });

  it("changing the select saves the new page size and refetches the timeline", async () => {
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("combobox", { name: "Rows shown" }));
    fireEvent.click(screen.getByRole("option", { name: "1000 rows" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/activity/display-settings",
        expect.objectContaining({ method: "PUT", body: JSON.stringify({ pageSize: 1000 }) })
      )
    );

    // The select now reflects the saved value, and the loader is asked to
    // re-run -- which is what actually re-fetches Activity at the new size.
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Rows shown" })).toHaveTextContent("1000 rows"));
    expect(revalidateMock).toHaveBeenCalled();
    expect(await screen.findByText("Page size saved")).toBeVisible();
  });

  it("offers exactly 50, 100, 250, 500 and 1000 rows", () => {
    authedFetchMock.mockImplementation(() => jsonResponse([]));
    render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("combobox", { name: "Rows shown" }));

    expect(screen.getAllByRole("option").map((option) => option.textContent)).toEqual(["50 rows", "100 rows", "250 rows", "500 rows", "1000 rows"]);
  });
});

describe("Clearing the Activity timeline asks first", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
  });

  function openTimeline() {
    render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );
  }

  it("says what clearing really takes and does nothing until it is confirmed", async () => {
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));
    openTimeline();

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));

    expect(await screen.findByText("Clear the Activity timeline?")).toBeVisible();
    expect(screen.getByText(/including the history behind automation decision explanations/)).toBeVisible();
    expect(screen.getByText(/This cannot be undone./)).toBeVisible();
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/activity/clear", expect.anything());
  });

  it("posts the clear once confirmed, says Activity cleared and reads the timeline again", async () => {
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));
    openTimeline();

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    fireEvent.click(await screen.findByRole("button", { name: "Clear activity" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/activity/clear", { method: "POST" }));
    expect(await screen.findByText("Activity cleared")).toBeVisible();
    expect(revalidateMock).toHaveBeenCalled();
    expect(screen.queryByText("Clear the Activity timeline?")).not.toBeInTheDocument();
  });

  it("keeps the timeline and says why when the clear is refused", async () => {
    authedFetchMock.mockImplementation((url: string, init?: RequestInit) => url === "/api/activity/clear" ? jsonResponse({}, false) : jsonResponse(init?.method === "POST" ? {} : []));
    openTimeline();

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    fireEvent.click(await screen.findByRole("button", { name: "Clear activity" }));

    expect(await screen.findByText("Activity could not be cleared.")).toBeVisible();
    expect(screen.queryByText("Activity cleared")).not.toBeInTheDocument();
  });
});

describe("Recycle bin: the days kept and emptying what has expired", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    loaderData.recycleBin = [];
    loaderData.recycleBinSettings = null;
    queryClient.clear();
  });

  const bin = () => {
    loaderData.recycleBin = [];
    loaderData.recycleBinSettings = { retentionDays: 30, maxSizeMb: 102400 } as never;
  };

  function openBin() {
    render(
      <MemoryRouter initialEntries={["/system/recycle-bin"]}>
        <SystemPage />
      </MemoryRouter>
    );
  }

  it("asks the server what an empty would take, lists it, and takes nothing until Empty them is pressed", async () => {
    bin();
    authedFetchMock.mockImplementation((url: string, init?: RequestInit) => jsonResponse(
      url === "/api/recycle-bin/cleanup/preview"
        ? { items: [{ id: "a" }, { id: "b" }], expiredCount: 2, overCapacityCount: 0, bytesFreed: 2048 }
        : init?.method === "POST" ? { removed: 2 } : []));
    openBin();

    fireEvent.click(screen.getByRole("button", { name: "Clean expired items" }));

    expect(await screen.findByText("Empty these from the recycle bin?")).toBeVisible();
    expect(screen.getByText(/2 items have passed their retention date\. Emptying frees .* This cannot be undone\./)).toBeVisible();
    expect(authedFetchMock.mock.calls.map(([path]) => String(path))).toContain("/api/recycle-bin/cleanup/preview");
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/recycle-bin/cleanup", expect.anything());

    fireEvent.click(screen.getByRole("button", { name: "Empty them" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/recycle-bin/cleanup", expect.objectContaining({ method: "POST" })));
    expect((await screen.findAllByText("Removed 2 expired recycle-bin items.")).length).toBeGreaterThan(0);
  });

  it("says plainly that nothing is ready and offers only Close when the preview is empty", async () => {
    bin();
    authedFetchMock.mockImplementation((url: string) => jsonResponse(
      url === "/api/recycle-bin/cleanup/preview" ? { items: [], expiredCount: 0, overCapacityCount: 0, bytesFreed: 0 } : []));
    openBin();

    fireEvent.click(screen.getByRole("button", { name: "Clean expired items" }));

    expect(await screen.findByText("Nothing is ready to be emptied")).toBeVisible();
    expect(screen.getByRole("button", { name: "Close" })).toBeVisible();
    expect(screen.queryByRole("button", { name: "Empty them" })).not.toBeInTheDocument();
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/recycle-bin/cleanup", expect.anything());
  });

  it("saves the days kept with a PUT of both numbers and says the settings were saved", async () => {
    bin();
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "PUT" ? { retentionDays: 45, maxSizeMb: 102400 } : []));
    openBin();

    fireEvent.change(screen.getByDisplayValue("30"), { target: { value: "45" } });
    fireEvent.click(screen.getByRole("button", { name: "Save recycle-bin settings" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith(
      "/api/recycle-bin/settings",
      expect.objectContaining({ method: "PUT", body: JSON.stringify({ retentionDays: 45, maxSizeMb: 102400 }) })));
    expect((await screen.findAllByText("Recycle-bin settings saved.")).length).toBeGreaterThan(0);
  });

  it("reads the bin and its settings again when RecycleBinChanged arrives, with no reload", async () => {
    vi.useFakeTimers();
    bin();
    authedFetchMock.mockImplementation(() => jsonResponse([]));
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    openBin();

    const handlers = [...(pushHandlers.get("RecycleBinChanged") ?? [])];
    expect(handlers.length).toBeGreaterThan(0);
    for (const handler of handlers) handler({ id: "recycle-bin" });
    await vi.advanceTimersByTimeAsync(1_000);

    const keys = invalidate.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown }).queryKey));
    expect(keys).toContain(JSON.stringify(["src", "recycle-bin"]));
    expect(keys).toContain(JSON.stringify(["src", "recycle-bin-settings"]));
    invalidate.mockRestore();
    vi.useRealTimers();
  });
});

/** #1073: a one-time note on the Health screen is cleared by hand, from the screen. */
describe("Health notes are clearable (#1073)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    loaderData.healthChecks = { checks: [], summary: { problemCount: 0 } };
    loaderData.monitoring = null;
  });

  it("a notice row has a Clear button that deletes it and refreshes; an ordinary row does not", async () => {
    loaderData.healthChecks = {
      checks: [
        { id: "notice:download-folders", category: "notice", name: "Downloads now go to the library's downloads folder", status: "problem", message: "Moved.", cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null },
        { id: "indexer:1", category: "indexer", name: "An indexer", status: "problem", message: "Down.", cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null }
      ],
      summary: { problemCount: 2 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "DELETE" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getAllByRole("button", { name: /^Clear:/ })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Clear: Downloads now go to the library's downloads folder" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/notices/notice%3Adownload-folders",
        expect.objectContaining({ method: "DELETE" })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });
});

describe("The unreachable-library note can confirm the files are gone (#1070)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    toastMock.success.mockReset();
    toastMock.error.mockReset();
    loaderData.healthChecks = { checks: [], summary: { problemCount: 0 } };
    loaderData.monitoring = null;
  });

  // Only the files-gone route gets the answer; every other request the screen makes gets an empty list.
  function answerFilesGone(body: unknown, ok = true) {
    authedFetchMock.mockImplementation((url: string) => (url.includes("files-gone") ? jsonResponse(body, ok) : jsonResponse([])));
  }

  function renderUnreachableNote() {
    loaderData.healthChecks = {
      checks: [
        { id: "notice:library-unreachable:lib-1", category: "notice", name: "Movies 4K looks unreachable", status: "problem", message: "m", cause: null, fix: null, detailsJson: JSON.stringify({ libraryId: "lib-1", missing: 30, total: 40, raisedUtc: "2026-09-30T00:00:00Z" }), lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null }
      ],
      summary: { problemCount: 1 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );
  }

  async function confirmFilesGone() {
    fireEvent.click(screen.getByRole("button", { name: "These files really are gone: Movies 4K looks unreachable" }));
    expect(await screen.findByText(/Nothing is deleted from your disk/)).toBeTruthy();
    expect(screen.getByText(/about 30 files/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Mark them as gone" }));
  }

  it("asks first, and shows the success message from the result", async () => {
    answerFilesGone({ markedGone: 29, cameBack: 1, couldNotChange: 0, libraryUnreachable: false, message: "Movies 4K: 29 files marked as gone, 1 came back and was left alone." });
    renderUnreachableNote();

    fireEvent.click(screen.getByRole("button", { name: "These files really are gone: Movies 4K looks unreachable" }));
    expect(authedFetchMock).not.toHaveBeenCalledWith(expect.stringContaining("files-gone"), expect.anything());
    fireEvent.click(await screen.findByRole("button", { name: "Mark them as gone" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Movies 4K: 29 files marked as gone, 1 came back and was left alone."));
    expect(toastMock.error).not.toHaveBeenCalled();
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("shows the reason when Deluno refuses because the drive is still unreachable", async () => {
    answerFilesGone({ markedGone: 0, cameBack: 0, couldNotChange: 0, libraryUnreachable: true, message: "Movies 4K is still not reachable, so Deluno changed nothing." });
    renderUnreachableNote();

    await confirmFilesGone();

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Movies 4K is still not reachable, so Deluno changed nothing."));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("shows the reason when there is nothing left to act on", async () => {
    answerFilesGone({ markedGone: 0, cameBack: 30, couldNotChange: 0, libraryUnreachable: false, message: "Every file Deluno tracks in Movies 4K is there now, so there is nothing to mark as gone." });
    renderUnreachableNote();

    await confirmFilesGone();

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining("nothing to mark as gone")));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("says a large answer is running in the background", async () => {
    answerFilesGone({ markedGone: 0, cameBack: 0, couldNotChange: 0, libraryUnreachable: false, inBackground: true, message: "Deluno is marking about 30 files as gone in the background." });
    renderUnreachableNote();

    await confirmFilesGone();

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Deluno is marking about 30 files as gone in the background."));
  });

  it("shows an error toast when the request fails or throws", async () => {
    answerFilesGone({}, false);
    renderUnreachableNote();
    await confirmFilesGone();
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledTimes(1));

    authedFetchMock.mockImplementation((url: string) => (url.includes("files-gone") ? Promise.reject(new Error("offline")) : jsonResponse([])));
    await confirmFilesGone();
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledTimes(2));
    expect(toastMock.success).not.toHaveBeenCalled();
  });

  it("offers These files really are gone next to Clear and posts to the library's route", async () => {
    loaderData.healthChecks = {
      checks: [
        { id: "notice:library-unreachable:lib-1", category: "notice", name: "Movies 4K looks unreachable", status: "problem", message: "Movies 4K: 30 of 40 files could not be found.", cause: null, fix: null, detailsJson: JSON.stringify({ libraryId: "lib-1", missing: 2, total: 5 }), lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null },
        { id: "notice:download-folders", category: "notice", name: "Other note", status: "problem", message: "Moved.", cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null }
      ],
      summary: { problemCount: 2 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getAllByRole("button", { name: /^These files really are gone/ })).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: /^Clear:/ })).toHaveLength(2);
    fireEvent.click(screen.getByRole("button", { name: "These files really are gone: Movies 4K looks unreachable" }));
    fireEvent.click(await screen.findByRole("button", { name: "Mark them as gone" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/filesystem/libraries/lib-1/files-gone",
        expect.objectContaining({ method: "POST" })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });
});

describe("A start-up step that did not finish can be run again or cleared (#1080)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    loaderData.healthChecks = { checks: [], summary: { problemCount: 0 } };
    loaderData.monitoring = null;
  });

  const row = (id: string, name: string) => ({ id, category: "system", name, status: "problem", message: "Did not finish.", cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-09-30T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null });

  it("Try again re-runs the step and Clear removes the row; a running job's row only snoozes", async () => {
    loaderData.healthChecks = {
      checks: [
        row("system:background-problem:startup:service-restart-on-failure", "Deluno may not restart itself"),
        row("system:background-problem:startup:collection-exclusions", "Collection exclusions were not moved"),
        row("system:background-problem:lane:import", "Background jobs: import lane keeps failing")
      ],
      summary: { problemCount: 3 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) =>
      jsonResponse(init?.method === "POST" ? { cleared: true, message: "It worked, so the note is gone." } : init?.method === "DELETE" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getAllByRole("button", { name: /^Try again:/ })).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: /^Clear:/ })).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: "Snooze 24h" })).toHaveLength(1);

    fireEvent.click(screen.getByRole("button", { name: "Try again: Deluno may not restart itself" }));
    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/background-problems/startup%3Aservice-restart-on-failure/retry",
        expect.objectContaining({ method: "POST" })
      )
    );
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());

    fireEvent.click(screen.getByRole("button", { name: "Clear: Collection exclusions were not moved" }));
    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/system/health/background-problems/startup%3Acollection-exclusions",
        expect.objectContaining({ method: "DELETE" })
      )
    );
  });
});

describe("Background jobs name each job in plain words (#1086)", () => {
  it("shows the label and never the job type", async () => {
    loaderData.jobs = [
      { id: "j1", jobType: "series.search.season", label: "Search for a season", source: "grab-rejected", sourceLabel: "A refused release", status: "completed", attempts: 1, createdUtc: "2026-09-30T00:00:00Z", lastError: null },
      { id: "j2", jobType: "library.search.tv", label: "Search the TV library", source: "download-health", sourceLabel: "The download health check", status: "failed", attempts: 3, maxAttempts: 3, nextAttemptUtc: null, createdUtc: "2026-09-30T00:00:00Z", lastError: "The indexer did not answer." }
    ] as never;
    loaderData.failedJobs = [loaderData.jobs[1]] as never;
    authedFetchMock.mockImplementation(() => jsonResponse([]));

    render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getByText("Search for a season")).toBeInTheDocument();
    expect(screen.getByText("Search the TV library")).toBeInTheDocument();
    expect(screen.getByText("Tried 3 times")).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/series\.search\.season|library\.search\.tv/);
    loaderData.jobs = [];
    loaderData.failedJobs = [];
  });
});

/** #1114: System's Background jobs are the jobs not about a title; a stopped one can be retried and dismissed there. */
describe("Background jobs are the ones not about a title (#1114)", () => {
  const base = { source: "schedule", sourceLabel: "The schedule", attempts: 1, maxAttempts: 3, nextAttemptUtc: null, createdUtc: "2026-10-01T00:00:00Z", startedUtc: null, completedUtc: null, workerId: null, payloadJson: null };
  const titleJob = { ...base, id: "t1", jobType: "movie.search", label: "Search for Sintel", status: "queued", lastError: null, relatedEntityType: "movie", relatedEntityId: "m1" };
  const refresh = { ...base, id: "b1", jobType: "library.refresh", label: "Refresh the whole library", status: "running", lastError: null, relatedEntityType: null, relatedEntityId: null };
  const backup = { ...base, id: "b2", jobType: "backup.create", label: "Nightly backup", status: "completed", lastError: null, relatedEntityType: null, relatedEntityId: "" };
  const failedBackground = { ...base, id: "b3", jobType: "metadata.refresh", label: "Refresh all metadata", status: "dead-letter", attempts: 3, lastError: "The metadata source did not answer.", relatedEntityType: null, relatedEntityId: null };
  const failedTitle = { ...base, id: "t2", jobType: "movie.search", label: "Search for Arrival", status: "dead-letter", attempts: 3, lastError: "No indexer answered.", relatedEntityType: "movie", relatedEntityId: "m2" };

  function renderSystem() {
    return render(
      <MemoryRouter initialEntries={["/system/audit"]}>
        <SystemPage />
      </MemoryRouter>
    );
  }

  afterEach(() => {
    loaderData.jobs = [];
    loaderData.failedJobs = [];
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
  });

  it("lists background jobs, running and recent, and leaves a job about a title to Activity", () => {
    loaderData.jobs = [titleJob, refresh, backup, failedTitle] as never;
    loaderData.failedJobs = [failedTitle] as never;
    renderSystem();

    expect(screen.getByText("Refresh the whole library")).toBeInTheDocument();
    expect(screen.getByText("Nightly backup")).toBeInTheDocument();
    expect(screen.queryByText("Search for Sintel")).toBeNull();
    expect(screen.queryByText("Search for Arrival")).toBeNull();
    expect(screen.queryByRole("button", { name: "Retry failed jobs" })).toBeNull();
  });

  it("shows a stopped background job first and retries it from there", async () => {
    loaderData.jobs = [refresh, failedBackground] as never;
    loaderData.failedJobs = [failedBackground, failedTitle] as never;
    authedFetchMock.mockImplementation(() => jsonResponse({ retried: 1 }));
    renderSystem();

    expect(screen.getByText("Refresh all metadata")).toBeInTheDocument();
    expect(screen.getByText("1 stopped")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry failed jobs" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/jobs/retry-failed", { method: "POST" }));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("opens a stopped background job to say why it failed and dismisses it there", async () => {
    loaderData.jobs = [failedBackground] as never;
    loaderData.failedJobs = [failedBackground] as never;
    authedFetchMock.mockImplementation(() => jsonResponse({}));
    renderSystem();

    fireEvent.click(screen.getByText("Refresh all metadata"));
    expect(await screen.findByText("Why it failed")).toBeInTheDocument();
    expect(screen.getAllByText("The metadata source did not answer.").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/jobs/b3/dismiss", { method: "POST" }));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });
});

/** #1108: both metadata credits sit on the installation card, where a person looks for About. */
describe("System installation credits (#1108)", () => {
  it("shows TMDb's notice and TheTVDB's credit under Credits", () => {
    loaderData.monitoring = { alerts: [] } as never;
    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );
    const credits = screen.getByRole("group", { name: "Credits" });
    expect(credits).toHaveTextContent("This product uses the TMDB API but is not endorsed or certified by TMDB.");
    expect(credits).toHaveTextContent("TV metadata provided by TheTVDB");
    expect(screen.getByRole("link", { name: "TMDb" })).toHaveAttribute("href", "https://www.themoviedb.org");
    expect(screen.getByRole("link", { name: "TheTVDB" })).toHaveAttribute("href", "https://thetvdb.com");
  });
});

/** #1229: the server's health push reaches an open System page and reads the health rows again, with no reload. */
describe("System follows the server's health push (#1229)", () => {
  afterEach(() => {
    vi.useRealTimers();
    pushHandlers.clear();
    authedFetchMock.mockReset();
  });

  it("reads the health checks, the providers and the processors again when HealthChanged arrives", async () => {
    vi.useFakeTimers();
    authedFetchMock.mockImplementation(() => jsonResponse([]));
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );

    const handlers = [...(pushHandlers.get("HealthChanged") ?? [])];
    expect(handlers.length).toBeGreaterThan(0);
    for (const handler of handlers) handler({ source: "system.health", status: "healthy", message: "0 problem(s) started, 2 cleared." });
    await vi.advanceTimersByTimeAsync(1_000);

    const keys = invalidate.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown }).queryKey));
    expect(keys).toContain(JSON.stringify(["src", "system-health"]));
    expect(keys).toContain(JSON.stringify(["src", "processor-connections-system"]));
    invalidate.mockRestore();
  });
});

/** #1228: the processor-output rows are the same problem as an import issue and its hand-off, so they are dismissed, not only snoozed. */
describe("Processor-output rows are dismissed with their issue (#1228)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    toastMock.success.mockReset();
    loaderData.healthChecks = { checks: [], summary: { problemCount: 0 } };
    loaderData.monitoring = null;
  });

  const row = (id: string, name: string, details: object | null) => ({
    id, category: "processor", name, status: "problem", message: "Weir named a file Deluno cannot find.", cause: null, fix: null,
    detailsJson: details ? JSON.stringify(details) : null, lastRunUtc: "2026-10-10T00:00:00Z", problemSinceUtc: null, snoozedUntilUtc: null
  });

  it("reads which issue a row mirrors, and nothing for any other row", () => {
    expect(outputIssueOf(row("processor-output-issue:c1", "x", { recoveryCaseId: "c1", recoveryMediaType: "movie" }))?.path).toBe("/api/movies/import-recovery/c1/dismiss");
    expect(outputIssueOf(row("processor-output-issue:c2", "x", { recoveryCaseId: "c2", recoveryMediaType: "series" }))?.path).toBe("/api/series/import-recovery/c2/dismiss");
    expect(outputIssueOf(row("processor-output-issue:c3", "x", null))).toBeNull();
    expect(outputIssueOf(row("indexer:1", "x", { recoveryCaseId: "c1" }))).toBeNull();
  });

  it("a Dismiss on the row dismisses the issue, and an ordinary problem keeps only Snooze", async () => {
    loaderData.healthChecks = {
      checks: [
        row("processor-output-issue:c1", "Retwice: missing from a processor's output", { recoveryCaseId: "c1", recoveryMediaType: "movie" }),
        row("indexer:1", "An indexer", null)
      ],
      summary: { problemCount: 2 }
    } as never;
    loaderData.monitoring = { alerts: [] } as never;
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "POST" ? {} : []));

    render(
      <MemoryRouter initialEntries={["/system"]}>
        <SystemPage />
      </MemoryRouter>
    );

    expect(screen.getAllByRole("button", { name: /^Dismiss:/ })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Dismiss: Retwice: missing from a processor's output" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/c1/dismiss", expect.objectContaining({ method: "POST" })));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
    expect(toastMock.success).toHaveBeenCalledWith("Dismissed. Nothing was deleted.");
  });
});

/** #1260: backup settings that could not be read are said so on System > Backups, and saving again repairs them and clears the message. */
describe("Backup settings problem (#1260)", () => {
  function renderBackups() {
    const router = createMemoryRouter([{ path: "*", element: <SystemPage /> }], { initialEntries: ["/system/backups"] });
    render(<RouterProvider router={router} />);
  }

  const settings = {
    enabled: false, frequency: "daily", timeOfDay: "03:00", retentionCount: 7, backupFolder: "", lastRunUtc: null, nextRunUtc: null
  };

  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
    loaderData.backupSettings = null;
  });

  it("shows the problem in plain words with what to do, and saving again makes it go", async () => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (loaderData as any).backupSettings = { ...settings, problem: "The backup settings file is damaged, so the standard settings are in use and scheduled backups are off until you save the settings again." };
    authedFetchMock.mockImplementation((_url: string, init?: RequestInit) => jsonResponse(init?.method === "PUT" ? settings : []));

    renderBackups();

    expect(screen.getByRole("alert")).toHaveTextContent("The backup settings file is damaged");
    expect(screen.getByRole("alert")).toHaveTextContent("Save the backup settings again to repair them.");

    fireEvent.click(screen.getByRole("button", { name: "Repair backup settings" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/backups/settings", expect.objectContaining({ method: "PUT" })));
    await waitFor(() => expect(screen.queryByRole("alert")).toBeNull());
  });

  it("shows nothing when the settings were read fine", () => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (loaderData as any).backupSettings = { ...settings, problem: null };
    authedFetchMock.mockImplementation(() => jsonResponse([]));

    renderBackups();

    expect(screen.queryByText("Save the backup settings again to repair them.")).toBeNull();
  });
});
