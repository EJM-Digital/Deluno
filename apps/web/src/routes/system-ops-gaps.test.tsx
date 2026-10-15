import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../lib/route-cache";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { SystemPage, systemSources } from "./system-screen";

/**
 * The System > Audit, Backups, Recycle bin and Updates screens, button by button (#1294). The screen is handed
 * its data the way the route cache hands it; everything it asks the server goes through the mocked sign-in
 * fetch, so each test shows the request that was made and the words the person then reads.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: toastMock, Toaster: () => null }));

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => vi.fn()
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
  recycleBinSettings: { retentionDays: 30, maxSizeMb: 102400 },
  retryWindows: [],
  searchCycles: [],
  settings: emptyPlatformSettingsSnapshot,
  systemInfo: null,
  systemTasks: [],
  updateStatus: null
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

type Answer = unknown | { __status: number; body: unknown };
/**
 * Answers by "METHOD path". Anything not listed is a read of a list and gets an empty list, which is what
 * the other cards on the screen ask for.
 */
function answer(routes: Record<string, Answer | ((init?: RequestInit) => Answer)>) {
  authedFetchMock.mockImplementation((url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url}`;
    if (!(key in routes)) return jsonResponse([]);
    const raw = routes[key];
    const value = typeof raw === "function" ? (raw as (init?: RequestInit) => Answer)(init) : raw;
    if (value && typeof value === "object" && "__status" in (value as object)) {
      const refused = value as { __status: number; body: unknown };
      return jsonResponse(refused.body, false, refused.__status);
    }
    return jsonResponse(value);
  });
}
const refuse = (status: number, body: unknown): Answer => ({ __status: status, body });

let bump: () => void = () => undefined;
/** The screen re-renders on demand, so a test can hand it a newer answer the way the route cache does. */
function Harness() {
  const [, setTick] = useState(0);
  bump = () => setTick((tick) => tick + 1);
  return <SystemPage />;
}

function renderAt(path: string) {
  const router = createMemoryRouter([{ path: "*", element: <Harness /> }], { initialEntries: [path] });
  return render(<RouterProvider router={router} />);
}

function resetAll() {
  authedFetchMock.mockReset();
  revalidateMock.mockReset();
  toastMock.success.mockReset();
  toastMock.error.mockReset();
  pushHandlers.clear();
  loaderData.activity = [];
  loaderData.backups = [];
  loaderData.backupSettings = null;
  loaderData.recycleBin = [];
  loaderData.recycleBinSettings = { retentionDays: 30, maxSizeMb: 102400 };
  loaderData.updateStatus = null;
}

const settings = (over: Record<string, unknown> = {}) => ({
  enabled: true, frequency: "weekly", timeOfDay: "02:30", retentionCount: 5, backupFolder: "D:/Backups", lastRunUtc: null, nextRunUtc: "2026-10-20T02:30:00Z", problem: null, ...over
});
const backup = (n: number) => ({ id: `b${n}`, fileName: `deluno-backup-${n}.zip`, fullPath: `D:/Backups/deluno-backup-${n}.zip`, sizeBytes: 2048 * n, createdUtc: `2026-10-1${n}T10:00:00Z`, reason: "manual" });

describe("Activity timeline (System > Audit)", () => {
  afterEach(resetAll);

  const event = (id: string, category: string, message: string, createdUtc = "2026-10-14T10:00:00Z") => ({
    id, category, label: null, message, severity: "info", detailsJson: null, relatedJobId: null, relatedEntityType: null, relatedEntityId: null, createdUtc
  });

  it("Clear asks first, names that it also removes the history behind automation decisions, and Cancel posts nothing", async () => {
    answer({});
    renderAt("/system/audit");

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Clear the Activity timeline?")).toBeInTheDocument();
    expect(within(dialog).getByText(/including the history behind automation decision explanations/)).toBeInTheDocument();
    expect(within(dialog).getByText(/This cannot be undone/)).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/activity/clear", expect.anything());
  });

  it("confirming Clear posts the clear, says Activity cleared and reads the timeline again", async () => {
    answer({ "POST /api/activity/clear": {} });
    renderAt("/system/audit");

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    fireEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "Clear activity" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/activity/clear", expect.objectContaining({ method: "POST" })));
    expect(await screen.findByText("Activity cleared")).toBeInTheDocument();
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("Find an event narrows the timeline by message or by kind", () => {
    loaderData.activity = [
      event("1", "download.completed", "Arrival finished downloading."),
      event("2", "import.completed", "Tenet was imported."),
      event("3", "search.started", "Searching for Dune.")
    ];
    renderAt("/system/audit");
    expect(screen.getByText("3 events")).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("Find an event"), { target: { value: "tenet" } });
    expect(screen.getByText("Tenet was imported.")).toBeInTheDocument();
    expect(screen.queryByText("Arrival finished downloading.")).toBeNull();
    expect(screen.getByText("1 event matching")).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("Find an event"), { target: { value: "search" } });
    expect(screen.getByText("Searching for Dune.")).toBeInTheDocument();
    expect(screen.queryByText("Tenet was imported.")).toBeNull();
  });

  it("the category chips count their events and narrow the list to one kind", () => {
    loaderData.activity = [
      event("1", "download.completed", "Arrival finished downloading."),
      event("2", "download.queued", "Dune was sent to the client."),
      event("3", "import.completed", "Tenet was imported."),
      event("4", "dispatch.failed", "Heat could not be sent.")
    ];
    renderAt("/system/audit");

    const chips = screen.getByRole("radiogroup", { name: "Filter by kind of event" });
    for (const name of ["All", "Download", "Import", "Search", "Error", "Notification", "System"]) {
      expect(within(chips).getByRole("radio", { name: new RegExp(`^${name}`) }), `chip ${name}`).toBeInTheDocument();
    }
    expect(within(chips).getByRole("radio", { name: /^All.*4/ })).toBeInTheDocument();
    expect(within(chips).getByRole("radio", { name: /^Download.*3/ })).toBeInTheDocument();
    expect(within(chips).getByRole("radio", { name: /^Import.*1/ })).toBeInTheDocument();

    fireEvent.click(within(chips).getByRole("radio", { name: /^Import/ }));
    expect(screen.getByText("Tenet was imported.")).toBeInTheDocument();
    expect(screen.queryByText("Arrival finished downloading.")).toBeNull();
    expect(screen.getByText("1 event matching")).toBeInTheDocument();
  });

  it("a pushed ActivityEventAdded event is added to the top without a reload, and the same id twice shows once", async () => {
    loaderData.activity = [event("old", "import.completed", "Tenet was imported.")];
    renderAt("/system/audit");
    const handlers = [...(pushHandlers.get("ActivityEventAdded") ?? [])];
    expect(handlers.length).toBeGreaterThan(0);

    const pushed = { id: "live-1", message: "Dune was grabbed.", category: "download.queued", label: "Sent to the client", severity: "info", createdUtc: "2026-10-14T11:00:00Z" };
    await act(async () => { for (const handler of handlers) handler(pushed); });
    await act(async () => { for (const handler of handlers) handler(pushed); });

    const items = screen.getAllByRole("listitem").map((item) => item.textContent ?? "");
    expect(items[0]).toContain("Dune was grabbed.");
    expect(items.filter((text) => text.includes("Dune was grabbed."))).toHaveLength(1);
    expect(items.some((text) => text.includes("Tenet was imported."))).toBe(true);
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/activity?pageSize=250", expect.anything());
  });

  it("keeps at most 50 live events on the screen", async () => {
    renderAt("/system/audit");
    const handlers = [...(pushHandlers.get("ActivityEventAdded") ?? [])];
    for (let n = 0; n < 60; n++) {
      await act(async () => {
        for (const handler of handlers) handler({ id: `live-${n}`, message: `Live event ${n}`, category: "system.note", label: "Note", severity: "info", createdUtc: "2026-10-14T11:00:00Z" });
      });
    }
    expect(screen.getByText("Live event 59")).toBeInTheDocument();
    expect(screen.getByText("Live event 10")).toBeInTheDocument();
    expect(screen.queryByText("Live event 9")).toBeNull();
  });
});

describe("Backups (System > Backups)", () => {
  afterEach(resetAll);

  it("shows the saved schedule: the switch, frequency, time, retention count and backup folder", () => {
    loaderData.backupSettings = settings();
    answer({});
    renderAt("/system/backups");

    expect(screen.getByRole("switch", { name: "Enable scheduled backups" })).toBeChecked();
    expect(screen.getByRole("combobox", { name: "Backup frequency" })).toHaveValue("weekly");
    expect(screen.getByLabelText("Backup time")).toHaveValue("02:30");
    expect(screen.getByLabelText("Backup retention count")).toHaveValue(5);
    expect(screen.getByDisplayValue("D:/Backups")).toBeInTheDocument();
    expect(screen.getByText(/Retains latest 5 backups/)).toBeInTheDocument();
    expect(screen.getByText(/Next run: /)).not.toHaveTextContent("Not scheduled");
  });

  it("lists the latest four backups with name, size and date and a Download link each, or says there are none", () => {
    loaderData.backupSettings = settings();
    loaderData.backups = [1, 2, 3, 4, 5].map(backup);
    answer({});
    const first = renderAt("/system/backups");

    expect(screen.getByText("5 backups")).toBeInTheDocument();
    for (const n of [1, 2, 3, 4]) expect(screen.getByText(`deluno-backup-${n}.zip`)).toBeInTheDocument();
    expect(screen.queryByText("deluno-backup-5.zip")).toBeNull();
    const link = screen.getAllByRole("link", { name: "Download" });
    expect(link).toHaveLength(4);
    expect(link[0]).toHaveAttribute("href", "/api/backups/b1/download");
    expect(screen.getByText(/2 KB|2\.0 KB/)).toBeInTheDocument();
    first.unmount();

    loaderData.backups = [];
    renderAt("/system/backups");
    expect(screen.getByText("No backups yet.")).toBeInTheDocument();
    expect(screen.getByText("No backups yet")).toBeInTheDocument();
  });

  it("Download latest appears only when a backup exists and points at the newest one", () => {
    loaderData.backupSettings = settings();
    loaderData.backups = [backup(3), backup(2)];
    answer({});
    const first = renderAt("/system/backups");
    expect(screen.getByRole("link", { name: "Download latest" })).toHaveAttribute("href", "/api/backups/b3/download");
    first.unmount();

    loaderData.backups = [];
    renderAt("/system/backups");
    expect(screen.queryByRole("link", { name: "Download latest" })).toBeNull();
  });

  it("Create backup posts the manual reason, says it is creating then that it is created, and lists the new backup", async () => {
    loaderData.backupSettings = settings();
    let created = false;
    answer({
      "POST /api/backups": () => { created = true; return backup(1); },
      "GET /api/backups": () => (created ? [backup(1)] : []),
      "GET /api/backups/settings": settings()
    });
    renderAt("/system/backups");

    fireEvent.click(screen.getByRole("button", { name: "Create backup" }));
    expect(await screen.findByText("Creating backup…")).toBeInTheDocument();

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith("/api/backups", expect.objectContaining({ method: "POST", body: JSON.stringify({ reason: "manual" }) }))
    );
    expect(await screen.findByText("Backup created")).toBeInTheDocument();
    expect(await screen.findByText("deluno-backup-1.zip")).toBeInTheDocument();
  });

  it("a failed Create backup shows the error text and no new backup", async () => {
    loaderData.backupSettings = settings();
    answer({ "POST /api/backups": refuse(500, "The backup folder is not writable.") });
    renderAt("/system/backups");

    fireEvent.click(screen.getByRole("button", { name: "Create backup" }));

    expect(await screen.findByText(/The backup folder is not writable/)).toBeInTheDocument();
    expect(screen.queryByText("Backup created")).toBeNull();
    expect(screen.getByText("No backups yet.")).toBeInTheDocument();
  });

  it("Save backup schedule is off until something changes, then saves the schedule and shows it saved", async () => {
    loaderData.backupSettings = settings();
    answer({ "PUT /api/backups/settings": settings({ retentionCount: 9, nextRunUtc: "2026-10-27T02:30:00Z" }) });
    renderAt("/system/backups");

    const save = screen.getByRole("button", { name: "Save backup schedule" });
    expect(save).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Backup retention count"), { target: { value: "9" } });
    expect(screen.getByText("Unsaved changes")).toBeInTheDocument();
    expect(save).not.toBeDisabled();

    fireEvent.click(save);

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/backups/settings",
        expect.objectContaining({ method: "PUT", body: expect.stringContaining('"retentionCount":9') })
      )
    );
    expect(await screen.findByText("Saved just now")).toBeInTheDocument();
    expect(screen.getByText(/Retains latest 9 backups/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save backup schedule" })).toBeDisabled();
  });

  it("a refused schedule save shows the server's words in the footer and keeps the edit", async () => {
    loaderData.backupSettings = settings();
    answer({ "PUT /api/backups/settings": refuse(400, "That backup folder does not exist.") });
    renderAt("/system/backups");

    fireEvent.change(screen.getByLabelText("Backup retention count"), { target: { value: "9" } });
    fireEvent.click(screen.getByRole("button", { name: "Save backup schedule" }));

    expect(await screen.findByText(/That backup folder does not exist/)).toBeInTheDocument();
    expect(screen.getByLabelText("Backup retention count")).toHaveValue(9);
  });

  it("a backup made elsewhere arrives when the server pushes BackupChanged, and a typed-in schedule edit is not overwritten", async () => {
    vi.useFakeTimers();
    loaderData.backupSettings = settings();
    answer({});
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    renderAt("/system/backups");

    fireEvent.change(screen.getByLabelText("Backup retention count"), { target: { value: "12" } });
    for (const handler of [...(pushHandlers.get("BackupChanged") ?? [])]) handler({ id: "b1" });
    await vi.advanceTimersByTimeAsync(1_000);
    const keys = invalidate.mock.calls.map((call) => JSON.stringify((call[0] as { queryKey: unknown }).queryKey));
    expect(keys).toContain(JSON.stringify(systemSources.backups.key));
    expect(keys).toContain(JSON.stringify(systemSources.backupSettings.key));
    invalidate.mockRestore();

    // The re-read answer: one more backup, and the schedule as saved elsewhere.
    loaderData.backups = [backup(1)];
    loaderData.backupSettings = settings({ retentionCount: 3 });
    vi.useRealTimers();
    act(() => bump());
    expect(await screen.findByText("deluno-backup-1.zip")).toBeInTheDocument();
    expect(screen.getByLabelText("Backup retention count")).toHaveValue(12);
  });

  const zip = () => new File(["PK"], "deluno-backup-1.zip", { type: "application/zip" });

  it("choosing a .zip uploads it for a restore preview by itself and says it is checking", async () => {
    loaderData.backupSettings = settings();
    let finish: (value: unknown) => void = () => undefined;
    authedFetchMock.mockImplementation((url: string, init?: RequestInit) =>
      url === "/api/backups/restore/preview" && init?.method === "POST"
        ? new Promise((resolve) => { finish = resolve; })
        : jsonResponse([]));
    renderAt("/system/backups");

    const input = screen.getByLabelText("Backup archive") as HTMLInputElement;
    expect(input).toHaveAttribute("accept", ".zip,application/zip");
    fireEvent.change(input, { target: { files: [zip()] } });

    expect(await screen.findByText("Checking backup…")).toBeInTheDocument();
    const call = authedFetchMock.mock.calls.find((c) => c[0] === "/api/backups/restore/preview")!;
    expect((call[1] as RequestInit).body).toBeInstanceOf(FormData);
    expect(((call[1] as RequestInit).body as FormData).get("file")).toBeInstanceOf(File);
    await act(async () => { finish({ ok: true, status: 200, json: () => Promise.resolve({ valid: true, message: "A valid backup.", manifest: null, warnings: [] }) }); });
    expect(await screen.findByText("A valid backup.")).toBeInTheDocument();
  });

  it("a preview that is refused shows the reason and offers no restore", async () => {
    loaderData.backupSettings = settings();
    answer({ "POST /api/backups/restore/preview": refuse(400, "That is not a Deluno backup.") });
    renderAt("/system/backups");

    fireEvent.change(screen.getByLabelText("Backup archive"), { target: { files: [zip()] } });

    expect(await screen.findByText(/That is not a Deluno backup/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Restore backup" })).toBeNull();
  });

  async function previewValidBackup() {
    loaderData.backupSettings = settings();
    answer({
      "POST /api/backups/restore/preview": { valid: true, message: "A valid Deluno backup.", manifest: { app: "Deluno", version: "1", createdUtc: "2026-10-10T10:00:00Z", reason: "manual", files: ["a.db", "b.db"] }, warnings: [] },
      "POST /api/backups/restore": { message: "Backup staged. Restart Deluno to apply it." }
    });
    renderAt("/system/backups");
    fireEvent.change(screen.getByLabelText("Backup archive"), { target: { files: [zip()] } });
    return screen.findByRole("button", { name: "Restore backup" });
  }

  it("Restore backup asks first and does nothing when cancelled", async () => {
    fireEvent.click(await previewValidBackup());

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Restore this backup?")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Restore now" })).toBeInTheDocument();
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/backups/restore", expect.anything());

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/backups/restore", expect.anything());
  });

  it("confirming uploads the archive, shows Restoring backup… then the server's restart message, and keeps it with no restart button", async () => {
    fireEvent.click(await previewValidBackup());
    fireEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "Restore now" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/backups/restore", expect.objectContaining({ method: "POST", body: expect.any(FormData) })));
    expect(await screen.findByText("Backup staged. Restart Deluno to apply it.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /restart/i })).toBeNull();
  });

  it("a restore that fails shows the error in the status and not a success", async () => {
    fireEvent.click(await previewValidBackup());
    answer({
      "POST /api/backups/restore/preview": { valid: true, message: "A valid Deluno backup.", manifest: null, warnings: [] },
      "POST /api/backups/restore": refuse(500, "The data folder is locked.")
    });
    fireEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "Restore now" }));

    expect(await screen.findByText(/The data folder is locked/)).toBeInTheDocument();
    expect(screen.queryByText(/Backup staged/)).toBeNull();
  });
});

describe("Recycle bin (System > Recycle bin)", () => {
  afterEach(resetAll);

  const item = (n: number, over: Record<string, unknown> = {}) => ({
    id: `r${n}`, libraryId: "lib-1", libraryName: "Movies", mediaType: "movies", originalPath: `D:/Movies/Film ${n}/Film ${n}.mkv`, recyclePath: `D:/bin/${n}`,
    isDirectory: false, sizeBytes: 1_048_576 * n, createdUtc: "2026-10-01T10:00:00Z", expiresUtc: "2026-10-31T10:00:00Z", titleSnapshotId: null, ...over
  });

  it("each item shows its library, media type, file or folder, original path, size, and for a removed title what Restore brings back", () => {
    loaderData.recycleBin = [item(1), item(2, { isDirectory: true, mediaType: "tv", titleSnapshotId: "snap-1" })];
    answer({});
    renderAt("/system/recycle-bin");

    expect(screen.getByText("2 stored items")).toBeInTheDocument();
    expect(screen.getByText("Movies · movies · file")).toBeInTheDocument();
    expect(screen.getByText("Movies · tv · folder")).toBeInTheDocument();
    expect(screen.getByText("D:/Movies/Film 1/Film 1.mkv")).toBeInTheDocument();
    expect(screen.getByText(/1.0 MB|1 MB/)).toBeInTheDocument();
    expect(screen.getAllByText(/removed .* expires /)).toHaveLength(2);
    expect(screen.getAllByText("Restore puts the title back in Deluno with these files, its monitoring and its history.")).toHaveLength(1);
  });

  it("an empty bin says so", () => {
    answer({});
    renderAt("/system/recycle-bin");
    expect(screen.getByText("Nothing stored")).toBeInTheDocument();
    expect(screen.getByText("Recycle bin is empty")).toBeInTheDocument();
  });

  it("the settings fields show the saved retention days and size cap and bound them to what the server accepts", () => {
    loaderData.recycleBinSettings = { retentionDays: 45, maxSizeMb: 2048 };
    answer({});
    renderAt("/system/recycle-bin");

    const days = screen.getByLabelText("Retention days");
    expect(days).toHaveValue(45);
    expect(days).toHaveAttribute("min", "1");
    expect(days).toHaveAttribute("max", "365");
    const size = screen.getByLabelText("Maximum size (MB)");
    expect(size).toHaveValue(2048);
    expect(size).toHaveAttribute("min", "1");
  });

  it("Save recycle-bin settings is off until something changes, then saves with PUT and says the settings are saved", async () => {
    loaderData.recycleBinSettings = { retentionDays: 45, maxSizeMb: 2048 };
    answer({ "PUT /api/recycle-bin/settings": { retentionDays: 60, maxSizeMb: 2048 } });
    renderAt("/system/recycle-bin");

    const save = screen.getByRole("button", { name: "Save recycle-bin settings" });
    expect(save).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Retention days"), { target: { value: "60" } });
    expect(save).not.toBeDisabled();
    fireEvent.click(save);

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/recycle-bin/settings",
        expect.objectContaining({ method: "PUT", body: JSON.stringify({ retentionDays: 60, maxSizeMb: 2048 }) })
      )
    );
    expect((await screen.findAllByText("Recycle-bin settings saved.")).length).toBeGreaterThan(0);
  });

  it("a settings save the server refuses shows its words and keeps the edit", async () => {
    answer({ "PUT /api/recycle-bin/settings": refuse(400, "Retention must be between 1 and 365 days.") });
    renderAt("/system/recycle-bin");

    fireEvent.change(screen.getByLabelText("Retention days"), { target: { value: "400" } });
    fireEvent.click(screen.getByRole("button", { name: "Save recycle-bin settings" }));

    expect((await screen.findAllByText(/Retention must be between 1 and 365 days/)).length).toBeGreaterThan(0);
    expect(screen.getByLabelText("Retention days")).toHaveValue(400);
  });

  it("Delete permanently asks first, Cancel keeps the item, and confirming calls DELETE and shows the message", async () => {
    loaderData.recycleBin = [item(1)];
    answer({ "DELETE /api/recycle-bin/r1": { message: "Film 1 was deleted for good." }, "GET /api/recycle-bin": [] });
    renderAt("/system/recycle-bin");

    fireEvent.click(screen.getByRole("button", { name: "Delete permanently" }));
    let dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Delete this item permanently?")).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/recycle-bin/r1", expect.anything());
    expect(screen.getByText("D:/Movies/Film 1/Film 1.mkv")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Delete permanently" }));
    dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete permanently" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/recycle-bin/r1", expect.objectContaining({ method: "DELETE" })));
    expect((await screen.findAllByText("Film 1 was deleted for good.")).length).toBeGreaterThan(0);
  });

  it("Restore posts the restore for that item and shows the server's message", async () => {
    loaderData.recycleBin = [item(1)];
    answer({ "POST /api/recycle-bin/r1/restore": { message: "Film 1 is back where it was." } });
    renderAt("/system/recycle-bin");

    fireEvent.click(screen.getByRole("button", { name: "Restore" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/recycle-bin/r1/restore", expect.objectContaining({ method: "POST" })));
    expect((await screen.findAllByText("Film 1 is back where it was.")).length).toBeGreaterThan(0);
  });

  it("Clean expired items first asks the server what an empty would take and shows it, with Empty them", async () => {
    loaderData.recycleBin = [item(1)];
    answer({ "GET /api/recycle-bin/cleanup/preview": { items: [{ id: "r1", originalPath: "D:/Movies/Film 1/Film 1.mkv", sizeBytes: 1_048_576, reason: "expired" }], totalBytes: 1_048_576 } });
    renderAt("/system/recycle-bin");

    fireEvent.click(screen.getByRole("button", { name: "Clean expired items" }));

    const dialog = await screen.findByRole("dialog");
    expect(authedFetchMock).toHaveBeenCalledWith("/api/recycle-bin/cleanup/preview", expect.anything());
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/recycle-bin/cleanup", expect.objectContaining({ method: "POST" }));
    expect(within(dialog).getByText("Empty these from the recycle bin?")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Empty them" })).toBeInTheDocument();
  });

  it("when nothing has expired the dialog says nothing is ready and offers Close, posting nothing", async () => {
    answer({ "GET /api/recycle-bin/cleanup/preview": { items: [], totalBytes: 0 } });
    renderAt("/system/recycle-bin");

    fireEvent.click(screen.getByRole("button", { name: "Clean expired items" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Nothing is ready to be emptied")).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/recycle-bin/cleanup", expect.objectContaining({ method: "POST" }));
  });

  it.each([
    [3, "Removed 3 expired recycle-bin items."],
    [1, "Removed 1 expired recycle-bin item."],
    [0, "No recycle-bin items were ready to expire."]
  ])("confirming Empty them posts the cleanup (%i removed), says so and closes the dialog", async (removed, sentence) => {
    loaderData.recycleBin = [item(1)];
    answer({
      "GET /api/recycle-bin/cleanup/preview": { items: [{ id: "r1", originalPath: "D:/Movies/Film 1/Film 1.mkv", sizeBytes: 1_048_576, reason: "expired" }], totalBytes: 1_048_576 },
      "POST /api/recycle-bin/cleanup": { removed }
    });
    renderAt("/system/recycle-bin");

    fireEvent.click(screen.getByRole("button", { name: "Clean expired items" }));
    fireEvent.click(await within(await screen.findByRole("dialog")).findByRole("button", { name: "Empty them" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/recycle-bin/cleanup", expect.objectContaining({ method: "POST" })));
    expect((await screen.findAllByText(sentence)).length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

describe("Updates (System > Updates)", () => {
  afterEach(resetAll);

  const status = (over: Record<string, unknown> = {}) => ({
    currentVersion: "1.2.0", channel: "stable", installKind: "windows-packaged", behaviorMode: "notify-only", isInstalled: true,
    canCheck: true, canDownload: false, canApply: false, updateAvailable: false, latestVersion: null, state: "idle",
    progressPercent: null, restartRequired: false, lastCheckedUtc: null, lastDownloadedUtc: null, message: "You are up to date.",
    lastError: null, notes: [], currentImageRef: null, currentImageDigest: null, changelog: null, appliedUpdates: [], ...over
  });
  const prefs = (over: Record<string, unknown> = {}) => ({ mode: "notify-only", channel: "stable", autoCheck: true, ...over });
  const action = (over: Record<string, unknown>, message: string, accepted = true) => ({ accepted, message, status: status(over) });

  function setup(initial: Record<string, unknown>, routes: Record<string, Answer | ((init?: RequestInit) => Answer)> = {}) {
    loaderData.updateStatus = status(initial);
    answer({ "GET /api/updates/preferences": prefs(), "GET /api/updates/status": status(initial), ...routes });
    renderAt("/system/updates");
  }

  it("a Docker install shows the image and digest and the pull-a-new-image guidance, and no in-app controls", () => {
    setup({ installKind: "docker", isInstalled: true, currentImageRef: "ghcr.io/example/deluno:1.2.0", currentImageDigest: "sha256:abc123" });

    expect(screen.getByText("ghcr.io/example/deluno:1.2.0")).toBeInTheDocument();
    expect(screen.getByText("sha256:abc123")).toBeInTheDocument();
    expect(screen.getByText("Docker updates are applied by pulling a new image and recreating the container.")).toBeInTheDocument();
    for (const name of ["Check now", "Download update", "Restart and apply"]) expect(screen.queryByRole("button", { name })).toBeNull();
  });

  it("a non-packaged install says the in-app controls need the packaged install and offers no buttons", () => {
    setup({ installKind: "manual", isInstalled: false, canCheck: false });

    expect(screen.getByText("In-app update controls require the packaged Windows install.")).toBeInTheDocument();
    for (const name of ["Check now", "Download update", "Restart and apply"]) expect(screen.queryByRole("button", { name })).toBeNull();
  });

  it("a packaged install offers Check now, Download update and Restart and apply", () => {
    setup({});
    for (const name of ["Check now", "Download update", "Restart and apply"]) expect(screen.getByRole("button", { name })).toBeInTheDocument();
    expect(screen.queryByText(/require the packaged Windows install/)).toBeNull();
  });

  it("Check now asks for an update check and shows the result in the status area", async () => {
    setup({}, { "POST /api/updates/check": action({ updateAvailable: true, latestVersion: "1.3.0", canDownload: true, message: "Version 1.3.0 is ready to download." }, "Version 1.3.0 is available.") });

    fireEvent.click(screen.getByRole("button", { name: "Check now" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/updates/check", expect.objectContaining({ method: "POST" })));
    expect(await screen.findByText("Version 1.3.0 is available.")).toBeInTheDocument();
    expect(screen.getByText("Version 1.3.0 is ready to download.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Download update" })).not.toBeDisabled();
  });

  it("a failed check shows as an error in the feed's own words", async () => {
    setup({}, { "POST /api/updates/check": action({ state: "error", lastError: "feed" }, "GitHub did not answer: rate limit exceeded.") });

    fireEvent.click(screen.getByRole("button", { name: "Check now" }));

    const message = await screen.findByText(/GitHub did not answer: rate limit exceeded\./);
    expect(message.closest("[role=status]")!.className).toContain("text-destructive");
  });

  it("a check that cannot reach the server shows the failure and reads the status again", async () => {
    setup({}, { "POST /api/updates/check": refuse(502, "The update feed is not reachable.") });

    fireEvent.click(screen.getByRole("button", { name: "Check now" }));

    expect(await screen.findByText(/The update feed is not reachable/)).toBeInTheDocument();
    await waitFor(() => expect(authedFetchMock.mock.calls.filter((call) => call[0] === "/api/updates/status").length).toBeGreaterThan(0));
  });

  it("Download update downloads, shows the progress, and ends at ready to restart", async () => {
    setup(
      { updateAvailable: true, latestVersion: "1.3.0", canDownload: true, message: "Version 1.3.0 is ready to download." },
      { "POST /api/updates/download": action({ updateAvailable: true, latestVersion: "1.3.0", canDownload: false, canApply: true, restartRequired: true, state: "ready", progressPercent: 100, message: "Ready to restart." }, "Update downloaded and checked.") }
    );

    fireEvent.click(screen.getByRole("button", { name: "Download update" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/updates/download", expect.objectContaining({ method: "POST" })));
    expect(await screen.findByText("Update downloaded and checked.")).toBeInTheDocument();
    expect(screen.getByText("Ready to restart.")).toBeInTheDocument();
    expect(screen.getByText(/Download progress: 100/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Restart and apply" })).not.toBeDisabled();
  });

  it.each([
    ["nothing staged", { canApply: false, restartRequired: false }],
    ["staged but no restart needed", { canApply: true, restartRequired: false }],
    ["restart needed but it cannot be applied", { canApply: false, restartRequired: true }]
  ])("Restart and apply is off when %s", (_name, over) => {
    setup(over);
    expect(screen.getByRole("button", { name: "Restart and apply" })).toBeDisabled();
  });

  it("Restart and apply is on when an update is staged and a restart is required, and posts the restart", async () => {
    setup(
      { canApply: true, restartRequired: true, updateAvailable: true, latestVersion: "1.3.0", state: "ready" },
      { "POST /api/updates/restart-now": action({ canApply: true, restartRequired: true, state: "restarting", message: "Restarting now." }, "Deluno is restarting into 1.3.0.") }
    );

    const button = screen.getByRole("button", { name: "Restart and apply" });
    expect(button).not.toBeDisabled();
    fireEvent.click(button);

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/updates/restart-now", expect.objectContaining({ method: "POST" })));
    expect(await screen.findByText("Deluno is restarting into 1.3.0.")).toBeInTheDocument();
  });

  it("a restart that is blocked shows as an error and not as success", async () => {
    setup(
      { canApply: true, restartRequired: true, state: "ready" },
      { "POST /api/updates/restart-now": action({ canApply: true, restartRequired: true, state: "error", message: "The update was not installed because Deluno could not back up its data first." }, "Backup failed, so the update was not applied.", false) }
    );

    fireEvent.click(screen.getByRole("button", { name: "Restart and apply" }));

    const message = await screen.findByText(/Backup failed, so the update was not applied\./);
    expect(message.closest("[role=status]")!.className).toContain("text-destructive");
    expect(screen.queryByText("Deluno is restarting into 1.3.0.")).toBeNull();
  });

  it("the three modes are offered with their descriptions", async () => {
    setup({});
    const mode = await screen.findByRole("combobox", { name: "Update behaviour" });
    expect(mode).toHaveValue("notify-only");
    expect(screen.getByText("Deluno checks for updates and notifies you, but does not download automatically.")).toBeInTheDocument();
    expect(within(mode).getAllByRole("option").map((option) => option.textContent)).toEqual([
      "Notify only", "Download in background", "Download and apply on next restart"
    ]);
    fireEvent.change(mode, { target: { value: "download-apply-on-restart" } });
    expect(screen.getByText("Deluno downloads updates automatically and stages them for the next restart.")).toBeInTheDocument();
    fireEvent.change(mode, { target: { value: "download-background" } });
    expect(screen.getByText("Deluno checks and downloads updates in the background. You restart when ready.")).toBeInTheDocument();
  });

  it("Save update preferences is off until a change, then saves the mode, channel and automatic check and shows it saved", async () => {
    setup({}, { "PUT /api/updates/preferences": prefs({ mode: "download-background", autoCheck: false }) });

    await screen.findByRole("combobox", { name: "Update behaviour" });
    expect(screen.getByRole("button", { name: "Save update preferences" })).toBeDisabled();
    fireEvent.change(screen.getByRole("combobox", { name: "Update behaviour" }), { target: { value: "download-background" } });
    fireEvent.click(screen.getByRole("switch", { name: "Check for updates automatically" }));
    expect(screen.getByText("Unsaved changes")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Save update preferences" }));

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith(
        "/api/updates/preferences",
        expect.objectContaining({ method: "PUT", body: JSON.stringify({ mode: "download-background", channel: "stable", autoCheck: false }) })
      )
    );
    expect(await screen.findByText("Saved just now")).toBeInTheDocument();
  });

  it("a refused preferences save shows the words and keeps the edit", async () => {
    setup({}, { "PUT /api/updates/preferences": refuse(400, "That update channel does not exist.") });

    await screen.findByRole("combobox", { name: "Update behaviour" });
    fireEvent.change(screen.getByRole("combobox", { name: "Update behaviour" }), { target: { value: "download-background" } });
    fireEvent.click(screen.getByRole("button", { name: "Save update preferences" }));

    expect(await screen.findByText(/That update channel does not exist/)).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Update behaviour" })).toHaveValue("download-background");
  });

  it("the update history lists each applied version with its date and a collapsed What changed, or says there are no notes", () => {
    setup({
      appliedUpdates: [
        { version: "1.2.0", appliedAtUtc: "2026-10-01T10:00:00Z", notes: "- **Faster** searches\n- Fewer false alarms" },
        { version: "1.1.0", appliedAtUtc: "2026-09-01T10:00:00Z", notes: null }
      ]
    });

    expect(screen.getByText("Update history")).toBeInTheDocument();
    expect(screen.getByText("1.1.0")).toBeInTheDocument();
    const changed = screen.getByText("What changed").closest("details")!;
    expect(changed).not.toHaveAttribute("open");
    expect(within(changed).getByText("Faster").tagName).toBe("STRONG");
    expect(within(changed).getByText("Fewer false alarms")).toBeInTheDocument();
    expect(screen.getByText("No notes for this version.")).toBeInTheDocument();
  });
});
