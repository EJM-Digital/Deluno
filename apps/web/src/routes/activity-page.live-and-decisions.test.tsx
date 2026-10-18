import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventItem, MovieImportRecoverySummary, SeriesImportRecoverySummary } from "../lib/api";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toasts = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toasts
}));

/** The screen's live listeners, kept by event name so a test can say "the server just pushed X". */
const live = vi.hoisted(() => ({ handlers: new Map<string, () => void>() }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useLiveEvent: (event: string, handler: () => void) => { live.handlers.set(event, handler); },
  useSignalREvent: (event: string, _group: unknown, handler: () => void) => { live.handlers.set(event, handler); }
}));

const jsonResponse = (body: unknown) => ({ ok: true, status: 200, json: async () => body });
const failure = (status = 400) => ({ ok: false, status, json: async () => ({ message: "no" }) });

const packCompleted = {
  id: "season-pack-completed",
  category: "filesystem.import.season-pack.completed",
  message: "Some Show season pack imported 9 season-pack files covering 9 episodes.",
  severity: "success",
  detailsJson: JSON.stringify({
    Skipped: [
      { SourcePath: "C:/Downloads/Some.Show.S01/Some.Show.S01.Extras.Making.Of.mkv", Reason: "The filename does not resolve to one or more canonical episodes in this show." },
      { SourcePath: "C:/Downloads/Some.Show.S01/Some.Show.S01E05.mkv", Reason: "Matched to episode(s) S01E05 but not imported: No video stream was detected." }
    ]
  }),
  relatedJobId: null,
  relatedEntityType: "series",
  relatedEntityId: "series-1",
  createdUtc: "2026-09-27T11:00:00Z"
} as ActivityEventItem;

const packPartlyCleared = {
  id: "season-pack-cleared",
  category: "filesystem.import.season-pack.leftovers-deleted",
  message: "Deleted 1 leftover file.",
  severity: "info",
  detailsJson: JSON.stringify({ Deleted: ["C:/Downloads/Some.Show.S01/Some.Show.S01.Extras.Making.Of.mkv"] }),
  relatedJobId: null,
  relatedEntityType: "series",
  relatedEntityId: "series-1",
  createdUtc: "2026-09-27T11:05:00Z"
} as ActivityEventItem;

const imported = {
  id: "event-2", category: "filesystem.import.completed", label: "Imported into the library", message: "Imported Arrival (2016).", severity: "success",
  detailsJson: null, relatedJobId: null, relatedEntityType: "movie", relatedEntityId: "movie-1", createdUtc: "2026-09-27T09:00:00Z"
} as ActivityEventItem;

let events: ActivityEventItem[] = [];

const loaderData = vi.hoisted(() => ({
  dispatches: [] as unknown[],
  failedJobs: [] as unknown[],
  jobs: [] as unknown[],
  movieRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] as unknown[] } as unknown as MovieImportRecoverySummary,
  seriesRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] } as unknown as SeriesImportRecoverySummary
}));

const revalidateMock = vi.fn();

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(loaderData, revalidateMock)
  };
});

const { ActivityPage, activitySources } = await import("./activity-page");
const { queryClient } = await import("../lib/route-cache");

const filterChoices = { types: [], sources: [], outcomes: [], reasons: [] };

function answer(path: string) {
  if (path.startsWith("/api/activity/filters")) return jsonResponse(filterChoices);
  if (path.startsWith("/api/activity?")) return jsonResponse({ items: events, nextPageToken: null, hasMore: false });
  if (path.startsWith("/api/integrations/processors/handoffs")) return jsonResponse([]);
  if (path.startsWith("/api/download-health")) return jsonResponse({ items: [], nextPageToken: null });
  if (path.startsWith("/api/download-clients/telemetry")) return jsonResponse({ summary: {}, clients: [], capturedUtc: "2026-09-27T08:00:00Z" });
  if (path.startsWith("/api/download-clients/history/dismissed")) return jsonResponse([]);
  return jsonResponse({});
}

function renderActivity(address = "/activity") {
  // The screen invalidates the app's one shared client, so that is the client it is rendered with.
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[address]}>
        <ActivityPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const calls = (prefix: string) => authedFetchMock.mock.calls.map((call) => String(call[0])).filter((path) => path.startsWith(prefix));

beforeEach(() => {
  events = [imported];
  queryClient.clear();
  queryClient.setDefaultOptions({ queries: { retry: false } });
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation(async (path: string) => answer(path));
  toasts.success.mockReset();
  toasts.error.mockReset();
  revalidateMock.mockReset();
  live.handlers.clear();
  loaderData.movieRecovery.recentCases.splice(0);
});

afterEach(() => vi.restoreAllMocks());

describe("Activity says when it could not read, and offers Try again (#1114)", () => {
  it("shows the history failing with nothing to list as 'The history could not be read' and asks the server again on Try again", async () => {
    authedFetchMock.mockImplementation(async (path: string) => (path.startsWith("/api/activity?") ? failure() : answer(path)));
    renderActivity("/activity");

    expect(await screen.findByText("The history could not be read")).toBeInTheDocument();
    const before = calls("/api/activity?").length;
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));

    await waitFor(() => expect(calls("/api/activity?").length).toBeGreaterThan(before));
  });

  it("shows a live step failing the same way and asks for that step's list again on Try again", async () => {
    authedFetchMock.mockImplementation(async (path: string) => (path.startsWith("/api/integrations/processors/handoffs") ? failure() : answer(path)));
    renderActivity("/activity?type=processing");

    expect(await screen.findByText("The history could not be read")).toBeInTheDocument();
    const before = calls("/api/integrations/processors/handoffs").length;
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));

    await waitFor(() => expect(calls("/api/integrations/processors/handoffs").length).toBeGreaterThan(before));
  });
});

describe("Activity follows the server with no reload (#1179)", () => {
  const invalidated = () => {
    const spy = vi.spyOn(queryClient, "invalidateQueries");
    return {
      keys: () => spy.mock.calls.map(([filters]) => JSON.stringify((filters as { queryKey?: unknown } | undefined)?.queryKey)),
      spy
    };
  };
  const push = (name: string) => act(() => { live.handlers.get(name)?.(); });
  const settle = () => new Promise((resolve) => setTimeout(resolve, 400));

  it("re-reads the movie and TV recovery lists once when a recovery case changes, so Needs a decision follows", async () => {
    renderActivity("/activity?status=needs-decision");
    await screen.findByText("Nothing needs a decision");
    const seen = invalidated();

    push("RecoveryCaseChanged");
    push("RecoveryCaseChanged");

    await waitFor(() => expect(seen.keys()).toContain(JSON.stringify(activitySources.movieRecovery.key)), { timeout: 3000 });
    await settle();
    expect(seen.keys().filter((key) => key === JSON.stringify(activitySources.movieRecovery.key))).toHaveLength(1);
    expect(seen.keys().filter((key) => key === JSON.stringify(activitySources.seriesRecovery.key))).toHaveLength(1);
  });

  it("re-reads the sends when a stage or a grab changes, one read for a burst, so a row moves from In progress to Imported or Failed", async () => {
    renderActivity("/activity");
    await screen.findByText("Imported Arrival (2016).");
    const seen = invalidated();

    for (const name of ["JourneyStageChanged", "DispatchGrabAttempt", "DispatchGrabCompleted", "DispatchDetected", "DispatchImportStarted", "DispatchImportCompleted"]) {
      push(name);
    }

    await waitFor(() => expect(seen.keys()).toContain(JSON.stringify(activitySources.dispatches.key)), { timeout: 3000 });
    await settle();
    expect(seen.keys().filter((key) => key === JSON.stringify(activitySources.dispatches.key))).toHaveLength(1);
  });

  it("re-reads the Processing step when a hand-off to the processor changes", async () => {
    renderActivity("/activity?type=processing");
    await screen.findByText("Nothing matches these filters");
    const before = calls("/api/integrations/processors/handoffs").length;

    push("ProcessorHandoffChanged");

    await waitFor(() => expect(calls("/api/integrations/processors/handoffs").length).toBeGreaterThan(before), { timeout: 3000 });
  });

  it("re-reads the Download health step when a finding changes", async () => {
    renderActivity("/activity?type=download-health");
    await screen.findByText("Nothing matches these filters");
    const before = calls("/api/download-health").length;

    push("DownloadHealthChanged");

    await waitFor(() => expect(calls("/api/download-health").length).toBeGreaterThan(before), { timeout: 3000 });
  });

  it("re-reads the Download client step once for a burst of client, progress and queue changes", async () => {
    renderActivity("/activity?type=download-client");
    await screen.findByText("Nothing matches these filters");
    const before = calls("/api/download-clients/telemetry").length;

    for (const name of ["ClientStateChanged", "DownloadProgress", "QueueItemAdded", "QueueItemRemoved", "QueueItemStatusChanged"]) {
      push(name);
    }

    await waitFor(() => expect(calls("/api/download-clients/telemetry").length).toBeGreaterThan(before), { timeout: 4000 });
    await new Promise((resolve) => setTimeout(resolve, 600));
    expect(calls("/api/download-clients/telemetry").length).toBe(before + 1);
  });
});

describe("A season pack's leftovers are deleted only when confirmed, and say what happened (#889)", () => {
  async function openPack() {
    events = [imported, packCompleted];
    renderActivity("/activity");
    await screen.findByText("Imported Arrival (2016).");
    await userEvent.click(screen.getByText("Some Show season pack imported 9 season-pack files covering 9 episodes."));
    await userEvent.click(screen.getByRole("button", { name: "Delete leftover files" }));
    return screen.getByRole("dialog", { name: "Delete leftover files?" });
  }

  it("Cancel on the confirmation deletes nothing", async () => {
    const confirm = await openPack();

    await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));

    expect(calls("/api/filesystem/")).toHaveLength(0);
    expect(toasts.success).not.toHaveBeenCalled();
    expect(toasts.error).not.toHaveBeenCalled();
  });

  it("says how many of them could not be removed when the server refuses some", async () => {
    authedFetchMock.mockImplementation(async (path: string) => (path.startsWith("/api/filesystem/") ? jsonResponse({ deleted: 1, refused: ["C:/Downloads/Some.Show.S01/Some.Show.S01E05.mkv"] }) : answer(path)));
    const confirm = await openPack();

    await userEvent.click(within(confirm).getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("Deleted 1 of 2. 1 could not be removed."));
    expect(toasts.success).not.toHaveBeenCalled();
  });

  it("says so when the server cannot delete at all, and leaves the confirmation open to try again", async () => {
    authedFetchMock.mockImplementation(async (path: string) => (path.startsWith("/api/filesystem/") ? failure(500) : answer(path)));
    const confirm = await openPack();

    await userEvent.click(within(confirm).getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("Those leftover files could not be deleted."));
    expect(screen.getByRole("dialog", { name: "Delete leftover files?" })).toBeInTheDocument();
  });

  it("lists only the files a later 'deleted' event has not already named", async () => {
    events = [imported, packCompleted, packPartlyCleared];
    renderActivity("/activity");
    await screen.findByText("Imported Arrival (2016).");
    await userEvent.click(screen.getByText("Some Show season pack imported 9 season-pack files covering 9 episodes."));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("Some.Show.S01E05.mkv")).toBeInTheDocument();
    expect(within(dialog).queryByText("Some.Show.S01.Extras.Making.Of.mkv")).not.toBeInTheDocument();
  });
});

describe("Stop wanting asks first, then stops (#1114)", () => {
  function addVanishedCase() {
    (loaderData.movieRecovery.recentCases as unknown[]).push({
      id: "vanished-1", title: "Sintel", failureKind: "vanished-downloading", summary: "The download disappeared from the client.",
      recommendedAction: "Search again or stop wanting it.", detailsJson: JSON.stringify({ DispatchId: "d1" }), detectedUtc: "2026-09-27T08:00:00Z", status: "open"
    });
  }

  it("asks 'Stop wanting Sintel?', does nothing on Cancel, then posts stop-wanting, says so and reads the list again", async () => {
    addVanishedCase();
    authedFetchMock.mockImplementation(async (path: string) => (path === "/api/movies/import-recovery/vanished-1/stop-wanting" ? jsonResponse({}) : answer(path)));
    renderActivity("/activity?status=needs-decision");

    await userEvent.click(screen.getByText("Sintel"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Stop wanting: Sintel" }));
    const confirm = screen.getByRole("dialog", { name: "Stop wanting Sintel?" });
    await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(calls("/api/movies/import-recovery/")).toHaveLength(0);

    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Stop wanting: Sintel" }));
    await userEvent.click(within(screen.getByRole("dialog", { name: "Stop wanting Sintel?" })).getByRole("button", { name: "Stop wanting" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/vanished-1/stop-wanting", expect.objectContaining({ method: "POST" })));
    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Deluno stopped wanting Sintel."));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });
});
