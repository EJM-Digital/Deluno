import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadQueueItem, JobQueueItem, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { QueuePage, queueSources } from "./queue-screen";

/** Which view of In progress the test opens (#1426): the hand-offs and imports moved off the live downloads. */
let viewEntry = "/queue";

/**
 * In progress, low rows: the drawers of the rows that have no drawer of their own, the transfer details drawer, the hand-off
 * fixes and the two lists the page reads. The routes behind them are tested on the real host.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn(), dismiss: vi.fn() }),
    realtimeToast: { success: vi.fn(), error: vi.fn(), message: vi.fn(), info: vi.fn(), warning: vi.fn() }
  };
});

const revalidateMock = vi.fn();
const emptyRecovery = { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] as unknown[] };
const queueLoaderData = {
  telemetry: {
    summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
    clients: [] as DownloadClientTelemetrySnapshot[],
    capturedUtc: new Date().toISOString()
  },
  dispatches: [] as unknown[],
  movieRecovery: { ...emptyRecovery },
  seriesRecovery: { ...emptyRecovery },
  libraries: [] as unknown[],
  settings: emptyPlatformSettingsSnapshot,
  jobs: [] as unknown[],
  failedJobs: [] as JobQueueItem[],
  healthRecords: [] as unknown[],
  processorConnections: [],
  processorHandoffs: [] as unknown[],
  activityEvents: [],
  orphans: [] as OrphanedDownloadItem[],
  heldReleases: [] as HeldReleaseRow[]
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => vi.fn()
}));

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(queueLoaderData, revalidateMock)
  };
});

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), text: () => Promise.resolve(JSON.stringify(body)), url: "test" } as Response);
}

const calls = (path: string) => authedFetchMock.mock.calls.filter(([url]) => String(url) === path);

function open() {
  render(
    <MemoryRouter initialEntries={[viewEntry]}>
      <QueuePage />
    </MemoryRouter>
  );
}

const release = "Nosferatu.2024.1080p.BluRay-GROUP";
const handoff = (overrides: Record<string, unknown> = {}) => ({
  id: "h1", libraryId: "library-1", mediaType: "movies", clientId: "qbit-client", queueItemId: "qbit-1", releaseName: release,
  sourcePath: "/downloads/nosferatu", processorName: "Weir", status: "failed", outputPath: null, importJobId: null,
  failureMessage: "Weir could not read the file.", createdUtc: new Date().toISOString(), updatedUtc: new Date().toISOString(), ...overrides
});

function queueItem(overrides: Partial<DownloadQueueItem> = {}): DownloadQueueItem {
  return {
    id: "qbit-1", clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies",
    title: "Example Movie", releaseName: "Example.Movie.2026.1080p.WEB-DL-GROUP", category: "movies", status: "completed", progress: 100,
    speedMbps: 0, etaSeconds: 0, sizeBytes: 4_294_967_296, downloadedBytes: 4_294_967_296, peers: 0, indexerName: "Fixture indexer",
    errorMessage: null, addedUtc: new Date(Date.now() - 600_000).toISOString(), sourcePath: null, libraryId: null, healthFindings: null,
    ...overrides
  };
}

function withClientItem(item: DownloadQueueItem) {
  queueLoaderData.telemetry = {
    ...queueLoaderData.telemetry,
    clients: [{
      clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
      capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
      summary: { activeCount: 0, queuedCount: 0, completedCount: 1, stalledCount: 0, processingCount: 0, importReadyCount: 1, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
      queue: [item], history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
    }]
  };
}

const heldRow = () => ({
  historyId: "held-1", mediaType: "movie", id: "movie-7", episodeId: null, title: "Dune", year: 2021, episodeLabel: null, libraryId: "library-1",
  releaseName: "Dune.2021.2160p.WEB-DL-GROUP", indexerName: "Fixture", indexerId: "indexer-1", downloadUrl: null, quality: "2160p", sizeBytes: 9_000_000_000,
  seeders: 12, reason: "Larger than the cap.", heldUtc: "2026-10-14T01:00:00Z"
}) as HeldReleaseRow;

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  authedFetchMock.mockImplementation(() => reply());
});

afterEach(() => {
  queueLoaderData.movieRecovery = { ...emptyRecovery };
  queueLoaderData.seriesRecovery = { ...emptyRecovery };
  queueLoaderData.failedJobs = [];
  queueLoaderData.heldReleases = [];
  queueLoaderData.processorHandoffs = [];
  queueLoaderData.dispatches = [];
  queueLoaderData.jobs = [];
  queueLoaderData.libraries = [];
  queueLoaderData.healthRecords = [];
  queueLoaderData.orphans = [];
  queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
});

describe("In progress: the hand-off fixes and the hand-off drawer", () => {
  it("a failed hand-off offers Send again, Import original, Search for another, Keep waiting and Dismiss together, each with a caption", async () => {
    queueLoaderData.processorHandoffs = [handoff()];
    open();
    fireEvent.click(screen.getAllByText(release)[0]!.closest('[role="row"]') ?? screen.getAllByText(release)[0]!);

    for (const name of [/Send to Weir again/, "Import original", "Search for another release", "Keep waiting", "Dismiss this hand-off"]) {
      expect(await screen.findByRole("button", { name })).toBeVisible();
    }
    const keep = screen.getByRole("button", { name: "Keep waiting" });
    expect((keep.parentElement?.parentElement?.textContent ?? "").replace("Keep waiting", "").trim().length).toBeGreaterThan(10);
  });

  it("Dismiss this hand-off posts the dismiss, closes the drawer and asks the page to read again", async () => {
    queueLoaderData.processorHandoffs = [handoff()];
    open();
    fireEvent.click(screen.getAllByText(release)[0]!.closest('[role="row"]') ?? screen.getAllByText(release)[0]!);

    fireEvent.click(await screen.findByRole("button", { name: "Dismiss this hand-off" }));

    await waitFor(() => expect(calls("/api/integrations/processors/handoffs/h1/dismiss")).toHaveLength(1));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Dismiss this hand-off" })).not.toBeInTheDocument());
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("the failed hand-off row shows its name, a select switch and Open to fix, and opens the hand-off drawer when the download left the client", async () => {
    queueLoaderData.processorHandoffs = [handoff()];
    open();

    expect(screen.getAllByText(release).length).toBeGreaterThan(0);
    expect(screen.getByRole("switch", { name: /Select Nosferatu/ })).toBeVisible();
    expect(screen.getAllByText("Open to fix").length).toBeGreaterThan(0);
    fireEvent.click(screen.getAllByText(release)[0]!.closest('[role="row"]') ?? screen.getAllByText(release)[0]!);

    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getByText("Outcome")).toBeVisible();
    expect(within(drawer).getByText("From")).toBeVisible();
    expect(within(drawer).getByText("/downloads/nosferatu")).toBeVisible();
  });

  it("the same row opens the download's own drawer when the download is still in the client", async () => {
    withClientItem(queueItem({ id: "qbit-1", releaseName: release, title: "Nosferatu", status: "processingFailed" as never }));
    queueLoaderData.processorHandoffs = [handoff()];
    open();

    fireEvent.click(screen.getAllByText(release)[0]!.closest('[role="row"]') ?? screen.getAllByText(release)[0]!);

    expect(await screen.findByRole("button", { name: "Preview import" })).toBeVisible();
    expect(screen.queryByText("The buttons for this row are on the row itself.")).not.toBeInTheDocument();
  });
});

describe("In progress: the attention drawer of a row with no drawer of its own", () => {
  it("a held release lists its release, quality, size, source and why it is held", async () => {
    queueLoaderData.heldReleases = [heldRow()];
    open();
    fireEvent.click(screen.getByText("Waiting for your OK: Dune.2021.2160p.WEB-DL-GROUP"));

    const drawer = await screen.findByRole("dialog");
    for (const label of ["Release", "Quality", "Size", "From", "Why it is held"]) expect(within(drawer).getByText(label)).toBeVisible();
    expect(within(drawer).getByText("2160p")).toBeVisible();
    expect(within(drawer).getByText("The buttons for this row are on the row itself.")).toBeVisible();
  });

  it("a recovery case lists what happened and what to do", async () => {
    queueLoaderData.movieRecovery = { ...emptyRecovery, openCount: 1, downloadFailedCount: 1, recentCases: [{
      id: "case-1", title: "Nosferatu", failureKind: "downloadFailed", summary: "The download failed.", recommendedAction: "Try it again.",
      detailsJson: "{}", detectedUtc: "2026-10-14T01:00:00Z"
    }] };
    open();
    fireEvent.click(screen.getByText("Nosferatu"));

    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getAllByText("What happened").length).toBeGreaterThan(0);
    expect(within(drawer).getByText("The download failed.")).toBeVisible();
    expect(within(drawer).getByText("What to do")).toBeVisible();
    expect(within(drawer).getByText("Try it again.")).toBeVisible();
    expect(within(drawer).getByText("The buttons for this row are on the row itself.")).toBeVisible();
  });

  it("a failed import job lists what happened and the attempts", async () => {
    queueLoaderData.failedJobs = [{
      id: "job-1", jobType: "filesystem.import.execute", label: "Import", source: "download-client", sourceLabel: "A download", status: "dead-letter",
      payloadJson: JSON.stringify({ title: "Arrival (2016)", fileName: "Arrival.2016.mkv" }), attempts: 3, createdUtc: "2026-10-14T01:00:00Z",
      scheduledUtc: "2026-10-14T01:00:00Z", startedUtc: null, completedUtc: null, leasedUntilUtc: null, workerId: null,
      lastError: "The destination folder is full.", relatedEntityType: null, relatedEntityId: null, idempotencyKey: null, dedupeKey: null
    } as JobQueueItem];
    open();
    fireEvent.click(screen.getByText("Arrival (2016)"));

    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getAllByText("What happened").length).toBeGreaterThan(0);
    expect(within(drawer).getByText("Attempts")).toBeVisible();
    expect(within(drawer).getByText("3")).toBeVisible();
    expect(within(drawer).getByText("The buttons for this row are on the row itself.")).toBeVisible();
  });

  it("a leftover folder lists what happened, its size and where it is", async () => {
    queueLoaderData.orphans = [{ path: "/downloads/complete/Old.Show.S01", name: "Old.Show.S01", libraryId: "library-1", libraryName: "TV", sizeBytes: 1_073_741_824, lastWrittenUtc: new Date().toISOString() } as OrphanedDownloadItem];
    open();
    fireEvent.click(screen.getByText("Old.Show.S01"));

    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getByText(/Left in the download folder/)).toBeVisible();
    expect(within(drawer).getByText("Size")).toBeVisible();
    expect(within(drawer).getByText("1.0 GB")).toBeVisible();
    expect(within(drawer).getByText("Where")).toBeVisible();
    expect(within(drawer).getByText("/downloads/complete/Old.Show.S01")).toBeVisible();
  });
});

describe("In progress: an import row opens the import drawer", () => {
  beforeEach(() => { viewEntry = "/queue?view=handoffs"; });
  afterEach(() => { viewEntry = "/queue"; });
  it("shows the outcome, detail, when, kind 'Import job', where it came from and where it went", async () => {
    queueLoaderData.jobs = [{
      id: "job-9", jobType: "filesystem.import.execute", label: "Import", source: "download-client", sourceLabel: "A download", status: "completed",
      payloadJson: JSON.stringify({ title: "Arrival (2016)", fileName: "Arrival.2016.mkv", sourcePath: "D:/Downloads/Arrival.2016.mkv", destinationPath: "E:/Movies/Arrival (2016)/Arrival.2016.mkv" }),
      attempts: 1, maxAttempts: 5, nextAttemptUtc: null, createdUtc: "2026-10-14T01:00:00Z", scheduledUtc: "2026-10-14T01:00:00Z", startedUtc: null,
      completedUtc: "2026-10-14T01:05:00Z", leasedUntilUtc: null, workerId: null, lastError: null, relatedEntityType: null, relatedEntityId: null, idempotencyKey: null, dedupeKey: null
    } as unknown as JobQueueItem];
    open();

    fireEvent.click(screen.getByText("Arrival (2016)"));

    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getByText("Outcome")).toBeVisible();
    expect(within(drawer).getByText("Imported as Arrival.2016.mkv")).toBeVisible();
    expect(within(drawer).getByText("When")).toBeVisible();
    expect(within(drawer).getByText("Kind")).toBeVisible();
    expect(within(drawer).getByText("Import job")).toBeVisible();
    expect(within(drawer).getByText("From")).toBeVisible();
    expect(within(drawer).getByText("D:/Downloads/Arrival.2016.mkv")).toBeVisible();
    expect(within(drawer).getByText("Destination")).toBeVisible();
    expect(within(drawer).getByText("E:/Movies/Arrival (2016)/Arrival.2016.mkv")).toBeVisible();
  });
});

describe("In progress: a held release is dismissed", () => {
  it("Dismiss posts the held release's dismiss and asks the page to read again so the row leaves", async () => {
    queueLoaderData.heldReleases = [heldRow()];
    open();

    fireEvent.click(screen.getByRole("button", { name: "Dismiss Dune.2021.2160p.WEB-DL-GROUP" }));

    await waitFor(() => expect(calls("/api/attention/held-releases/held-1/dismiss")).toHaveLength(1));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });
});

describe("In progress: an import with no file location", () => {
  it("Preview import says the client has not reported a completed file location, and sends nothing", async () => {
    withClientItem(queueItem({ status: "completed", sourcePath: null, libraryId: "library-none" }));
    open();
    fireEvent.click(screen.getByText("Example.Movie.2026.1080p.WEB-DL-GROUP").closest('[role="row"]')!);

    fireEvent.click(await screen.findByRole("button", { name: "Preview import" }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith(expect.stringContaining("has not reported a completed file location")));
    expect(calls("/api/filesystem/import/preview")).toHaveLength(0);
  });
});

describe("In progress: the lists the page reads", () => {
  it("the download-health list is asked for 30 records and hands them to the page", async () => {
    const record = { clientId: "qbit-client", queueItemId: "q9", kind: "client-stalled", releaseName: "Stalled.Release.2026", evidence: "No bytes for 40 minutes.", severity: "warning", strikeCount: 2, lastObservedUtc: "2026-10-14T01:00:00Z" };
    authedFetchMock.mockImplementation(() => reply({ items: [record], totalCount: 1 }));

    const records = await queueSources.healthRecords.fetch();

    expect(authedFetchMock.mock.calls.some(([url]) => String(url) === "/api/download-health?pageSize=30")).toBe(true);
    expect(records).toEqual([record]);
  });

  it("a failed job buried under newer work is asked for by name, listed under In progress and dismissed from its row", async () => {
    const buried = { id: "job-old", jobType: "filesystem.import.execute", status: "dead-letter", payloadJson: JSON.stringify({ title: "Buried (2001)" }), attempts: 5, lastError: "The disk was full." };
    authedFetchMock.mockImplementation((url: string) => String(url).startsWith("/api/jobs?")
      ? reply({ items: [buried, { id: "job-ok", jobType: "x", status: "completed" }], totalCount: 2, page: 1, pageSize: 500 })
      : reply());

    const failed = await queueSources.failedJobs.fetch();

    expect(authedFetchMock.mock.calls.some(([url]) => String(url).startsWith("/api/jobs?failed=true"))).toBe(true);
    expect(failed.map((job) => job.id)).toEqual(["job-old"]);
    queueLoaderData.failedJobs = failed;
    open();
    expect(screen.getByText("Buried (2001)")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(calls("/api/jobs/job-old/dismiss")).toHaveLength(1));
  });
});

describe("In progress: the transfer details drawer of a failed send", () => {
  const dispatch = (overrides: Record<string, unknown> = {}) => ({
    id: "d1", releaseName: "Failing.Film.2026.1080p-GRP", mediaType: "movies", entityType: "movie", entityId: "m1", status: "failed",
    grabStatus: "failed", grabMessage: "The indexer refused the download.", importStatus: null, importFailureMessage: null, importFailureCode: null,
    grabFailureCode: "indexer-refused", indexerName: "Fixture indexer", downloadClientName: "qBittorrent", downloadClientId: "qbit-client",
    torrentHashOrItemId: "abc", detectedUtc: null, attemptCount: 2, createdUtc: "2026-10-14T01:00:00Z", importedFilePath: null, downloadedBytes: null,
    failure: null, ...overrides
  });
  const event = (id: string, eventType: string, detailsJson: string | null) => ({ id, eventType, detailsJson, timestamp: "2026-10-14T01:00:00Z" });

  function openDispatch(summary: Record<string, unknown>, detail: Record<string, unknown> | "fails") {
    queueLoaderData.dispatches = [summary];
    authedFetchMock.mockImplementation((url: string) => {
      if (String(url).startsWith("/api/v1/download-dispatches/")) {
        return detail === "fails" ? reply({ message: "nope" }, false, 404) : reply(detail);
      }
      return reply();
    });
    open();
    fireEvent.click(screen.getAllByText(String(summary.releaseName))[0]!.closest('[role="row"]') ?? screen.getAllByText(String(summary.releaseName))[0]!);
  }

  it("lists the journey: release, indexer, client, grab, detected, import and attempts", async () => {
    const d = dispatch();
    openDispatch(d, { dispatch: d, timeline: [] });

    const drawer = await screen.findByRole("dialog");
    await within(drawer).findByText("Transfer journey");
    for (const label of ["Release", "Indexer", "Client", "Grab", "Detected", "Import", "Attempts"]) expect(within(drawer).getAllByText(label).length).toBeGreaterThan(0);
    expect(within(drawer).getByText("Fixture indexer")).toBeVisible();
    expect(within(drawer).getByText("The indexer refused the download.")).toBeVisible();
    expect(within(drawer).getByText("Not detected by the client")).toBeVisible();
    expect(within(drawer).getByText("2")).toBeVisible();
  });

  it("shows the integration finding and the file and outcome when the send carries them", async () => {
    const d = dispatch({
      importedFilePath: "E:/Movies/Failing Film (2026)/Failing.Film.mkv", downloadedBytes: 2_147_483_648,
      failure: { kind: "transient", message: "Timed out.", summary: "The client did not answer.", nextAction: "Try again in a moment.", retryState: "RetryScheduled", attempts: 2, code: "timeout", serviceName: "qBittorrent", operation: "add", httpStatus: null, externalId: null, retryAfterUtc: null }
    });
    openDispatch(d, { dispatch: d, timeline: [] });

    const drawer = await screen.findByRole("dialog");
    await within(drawer).findByText("Integration finding");
    expect(within(drawer).getByText("The client did not answer.")).toBeVisible();
    expect(within(drawer).getByText("File and outcome")).toBeVisible();
    expect(within(drawer).getByText("E:/Movies/Failing Film (2026)/Failing.Film.mkv")).toBeVisible();
    expect(within(drawer).getByText("2.0 GB")).toBeVisible();
    expect(within(drawer).getByText("indexer-refused")).toBeVisible();
  });

  it("reads the file provenance from the newest timeline event: original release, source, destination, rename, rules, processor and transfer", async () => {
    const d = dispatch();
    const provenance = JSON.stringify({
      releaseNameEvidence: "Failing.Film.2026.1080p-GRP", sourcePath: "D:/dl/Failing.Film.mkv", destinationPath: "E:/Movies/Failing Film (2026)/Failing Film (2026).mkv",
      rename: "Failing Film (2026).mkv", matchedRuleNames: "Movies rule", processorName: "Weir", preferredTransferMode: "hardlink", transferModeUsed: "copy", usedFallback: true,
      preferencePlanId: "plan-1", preferencePlanVersion: "v3", preferencePlanHash: "abc123", workflowCleanup: "Source removed"
    });
    openDispatch(d, { dispatch: d, timeline: [event("e1", "file_provenance_recorded", provenance)] });

    const drawer = await screen.findByRole("dialog");
    await within(drawer).findByText("File provenance");
    for (const text of ["Failing.Film.2026.1080p-GRP", "D:/dl/Failing.Film.mkv", "E:/Movies/Failing Film (2026)/Failing Film (2026).mkv", "Failing Film (2026).mkv", "Movies rule", "Weir", "hardlink requested · copy used · fallback required", "plan-1 · v3", "abc123", "Source removed"]) {
      expect(within(drawer).getAllByText(text).length).toBeGreaterThan(0);
    }
    for (const label of ["Original release", "Source file", "Destination file", "Rename", "Matched rules", "Processor", "Transfer", "Preference plan", "Plan proof", "Cleanup"]) expect(within(drawer).getByText(label)).toBeVisible();
  });

  it("lists the recorded timeline with event labels and details, and leaves out an import failure already shown", async () => {
    const d = dispatch({ importFailureMessage: "The destination folder is full and nothing could be written." });
    openDispatch(d, {
      dispatch: d,
      timeline: [
        event("e1", "grab_sent", JSON.stringify({ client: "qBittorrent", attempt: 2 })),
        event("e2", "import_failed", JSON.stringify({ reason: "The destination folder is full and nothing could be written." }))
      ]
    });

    const drawer = await screen.findByRole("dialog");
    await within(drawer).findByText("Recorded timeline");
    expect(within(drawer).getByText("Grab Sent")).toBeVisible();
    expect(within(drawer).getByText("Client: qBittorrent · Attempt: 2")).toBeVisible();
    expect(within(drawer).getByText("Import Failed")).toBeVisible();
    expect(within(drawer).getAllByText(/The destination folder is full/)).toHaveLength(1);
    expect(within(drawer).getByText("2 events")).toBeVisible();
  });

  it("says the client has reported no additional transfer events when the timeline is empty", async () => {
    const d = dispatch();
    openDispatch(d, { dispatch: d, timeline: [] });

    const drawer = await screen.findByRole("dialog");
    expect(await within(drawer).findByText("The client has not reported any additional transfer events yet.")).toBeVisible();
    expect(within(drawer).getByText("No events recorded")).toBeVisible();
  });

  it("when the detail fetch fails it toasts and the drawer reads 'Transfer details unavailable'", async () => {
    openDispatch(dispatch(), "fails");

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    const drawer = await screen.findByRole("dialog");
    expect(await within(drawer).findByText("Transfer details unavailable")).toBeVisible();
  });

  it("Dismiss this download stops counting a failed send, closes the drawer and says so (#1270)", async () => {
    const d = dispatch();
    openDispatch(d, { dispatch: d, timeline: [] });

    const drawer = await screen.findByRole("dialog");
    fireEvent.click(await within(drawer).findByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(calls("/api/v1/download-dispatches/d1?reason=dismissed")).toHaveLength(1));
    expect(authedFetchMock.mock.calls.find(([url]) => String(url) === "/api/v1/download-dispatches/d1?reason=dismissed")?.[1]).toMatchObject({ method: "DELETE" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

