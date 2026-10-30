import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventItem, MovieImportRecoverySummary, SeriesImportRecoverySummary } from "../lib/api";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const toasts = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toasts
}));

/** The screen's live listeners, kept by event name so a test can say "the server just pushed X". */
const live = vi.hoisted(() => ({ handlers: new Map<string, (...args: unknown[]) => void>() }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useLiveEvent: (event: string, handler: () => void) => { live.handlers.set(event, handler); },
  useSignalREvent: (event: string, _group: unknown, handler: (...args: unknown[]) => void) => { live.handlers.set(event, handler); }
}));

const jsonResponse = (body: unknown) => ({ ok: true, status: 200, json: async () => body, text: async () => JSON.stringify(body) });
const failure = (status: number) => ({ ok: false, status, json: async () => ({}), text: async () => "" });

const emptyRecovery = () => ({ openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] as unknown[] });
const loaderData = vi.hoisted(() => ({
  dispatches: [] as unknown[],
  failedJobs: [] as unknown[],
  jobs: [] as unknown[],
  movieRecovery: {} as MovieImportRecoverySummary,
  seriesRecovery: {} as SeriesImportRecoverySummary
}));
const revalidateMock = vi.fn();

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(loaderData, revalidateMock)
  };
});

const { ActivityPage } = await import("./activity-page");
const { queryClient } = await import("../lib/route-cache");

const filterChoices = {
  types: [{ value: "search", label: "Searched" }],
  sources: [{ value: "indexer", label: "An indexer" }],
  outcomes: [{ value: "problem", label: "Went wrong" }],
  reasons: [{ value: "language", label: "Not in a language you want" }]
};

const event = (overrides: Partial<ActivityEventItem> & { id: string }) => ({
  category: "filesystem.import.completed", label: "Imported into the library", message: `Message of ${overrides.id}`, severity: "success", detailsJson: null,
  relatedJobId: null, relatedEntityType: "movie", relatedEntityId: "movie-1", createdUtc: "2026-09-27T09:00:00Z", ...overrides
}) as ActivityEventItem;

const job = (overrides: Record<string, unknown>) => ({
  id: "job-x", jobType: "movie.search", label: "Search for a movie", source: "scheduler", sourceLabel: "A scheduled pass", status: "queued",
  payloadJson: "{}", attempts: 0, createdUtc: "2026-09-27T08:00:00Z", scheduledUtc: "2026-09-27T08:00:00Z", startedUtc: null,
  completedUtc: null, leasedUntilUtc: null, workerId: null, lastError: null, relatedEntityType: "movie", relatedEntityId: "movie-1",
  idempotencyKey: null, dedupeKey: null, maxAttempts: 3, lastAttemptUtc: null, nextAttemptUtc: null, ...overrides
});

const dispatch = (overrides: Record<string, unknown>) => ({
  id: "dispatch-x", libraryId: "lib", mediaType: "movie", entityType: "movie", entityId: "movie-1", releaseName: "Arrival.2016.1080p",
  indexerName: "NZBgeek", downloadClientId: "c1", downloadClientName: "SABnzbd", status: "sent", notesJson: null,
  createdUtc: "2026-09-27T07:00:00Z", ...overrides
});

let events: ActivityEventItem[] = [];
/** A test's own answer for a path, tried before the default; return undefined to fall through. */
let override: (path: string, init?: RequestInit) => unknown = () => undefined;

function answer(path: string, init?: RequestInit) {
  const special = override(path, init);
  if (special !== undefined) return special;
  if (path.startsWith("/api/activity/filters")) return jsonResponse(filterChoices);
  if (path.startsWith("/api/activity?")) return jsonResponse({ items: events, nextPageToken: null, hasMore: false });
  if (path.startsWith("/api/integrations/processors/handoffs")) return jsonResponse([]);
  if (path.startsWith("/api/download-health")) return jsonResponse({ items: [], nextPageToken: null });
  if (path.startsWith("/api/download-clients/telemetry")) return jsonResponse({ summary: {}, clients: [], capturedUtc: "2026-09-27T08:00:00Z" });
  if (path.startsWith("/api/download-clients/history/dismissed")) return jsonResponse([]);
  return jsonResponse({});
}

function LocationProbe() {
  const location = useLocation();
  return <output aria-label="address">{location.pathname + location.search}</output>;
}

function renderActivity(address = "/queue?view=history") {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[address]}>
        <ActivityPage />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const activityCalls = () => authedFetchMock.mock.calls.map((call) => String(call[0])).filter((path) => path.startsWith("/api/activity?"));

beforeEach(() => {
  events = [];
  override = () => undefined;
  queryClient.clear();
  queryClient.setDefaultOptions({ queries: { retry: false } });
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation(async (path: string, init?: RequestInit) => answer(path, init));
  toasts.success.mockReset();
  toasts.error.mockReset();
  revalidateMock.mockReset();
  live.handlers.clear();
  window.localStorage.clear();
  loaderData.jobs = [];
  loaderData.failedJobs = [];
  loaderData.dispatches = [];
  loaderData.movieRecovery = emptyRecovery() as unknown as MovieImportRecoverySummary;
  loaderData.seriesRecovery = emptyRecovery() as unknown as SeriesImportRecoverySummary;
});

const stopped = (overrides: Record<string, unknown> = {}) => job({
  id: "job-9", label: "Import Arrival", status: "dead-letter", attempts: 3, jobType: "filesystem.import.execute",
  completedUtc: "2026-09-27T09:30:00Z", lastError: "The destination file already exists.", ...overrides
});

describe("Activity's drawers and their failures (#1329)", () => {
  it("Dismiss on a failed job the server cannot dismiss says so in a toast and leaves the drawer open", async () => {
    loaderData.failedJobs = [stopped()];
    override = (path) => (path === "/api/jobs/job-9/dismiss" ? failure(500) : undefined);
    renderActivity("/queue?view=history&status=failed");

    await userEvent.click(await screen.findByText("Import Arrival"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss" }));

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That job could not be dismissed."));
    expect(toasts.success).not.toHaveBeenCalled();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("Try again that fails says 'That job could not be tried again.' and keeps the drawer open", async () => {
    loaderData.failedJobs = [stopped()];
    override = (path) => (path === "/api/jobs/job-9/retry" ? failure(500) : undefined);
    renderActivity("/queue?view=history&status=failed");

    await userEvent.click(await screen.findByText("Import Arrival"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Try again" }));

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That job could not be tried again."));
    expect(toasts.success).not.toHaveBeenCalled();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("Try again on a failed import job posts the retry for that job only, says it is being tried again, closes the drawer and reads again", async () => {
    loaderData.failedJobs = [stopped(), stopped({ id: "job-10", label: "Import Sintel" })];
    renderActivity("/queue?view=history&status=failed");

    await userEvent.click(await screen.findByText("Import Arrival"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Try again" }));

    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Import Arrival is being tried again"));
    const retries = authedFetchMock.mock.calls.filter((call) => String(call[0]).endsWith("/retry"));
    expect(retries).toHaveLength(1);
    expect(retries[0][0]).toBe("/api/jobs/job-9/retry");
    expect(retries[0][1]).toEqual({ method: "POST" });
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("the job drawer shows the run facts: attempts, scheduled, 'Not started', 'Not finished' and the worker, and what was asked", async () => {
    loaderData.jobs = [job({
      id: "job-facts", label: "Search for Arrival", status: "queued", attempts: 2, workerId: "worker-7", startedUtc: null, completedUtc: null,
      payloadJson: JSON.stringify({ movieId: "movie-1" })
    })];
    renderActivity("/queue?view=history&status=in-progress");

    await userEvent.click(await screen.findByText("Search for Arrival"));
    const dialog = screen.getByRole("dialog");
    const fact = (label: string) => within(dialog).getByText(label).parentElement?.textContent ?? "";
    expect(fact("Attempts")).toContain("2");
    expect(fact("Started")).toContain("Not started");
    expect(fact("Finished")).toContain("Not finished");
    expect(fact("Worker")).toContain("worker-7");
    expect(fact("Scheduled")).not.toContain("Invalid");
    expect(within(dialog).getByText("What was asked")).toBeInTheDocument();
  });

  it("an event's 'What it was about' lists the title, where it came from, the reason in words and each newline or semicolon separated file as a link to that file's history", async () => {
    events = [event({
      id: "ev-files", message: "Refused two files", title: "Arrival", sourceName: "NZBgeek", reasonGroup: "language",
      filePath: "C:/Downloads/Arrival/a.mkv\nC:/Downloads/Arrival/b.mkv;C:/Downloads/Arrival/c.mkv"
    })];
    renderActivity();

    await userEvent.click(await screen.findByText("Refused two files"));
    const dialog = screen.getByRole("dialog");
    const about = within(dialog).getByText("What it was about").closest("section") ?? dialog;
    expect(about.textContent).toContain("Arrival");
    expect(about.textContent).toContain("NZBgeek");
    expect(about.textContent).toContain("Not in a language you want");
    for (const name of ["a.mkv", "b.mkv", "c.mkv"]) {
      const link = within(dialog).getByRole("link", { name: `C:/Downloads/Arrival/${name}` });
      expect(link).toHaveAttribute("href", `/queue?view=history&file=${encodeURIComponent(`C:/Downloads/Arrival/${name}`)}`);
    }
  });

  it("an event drawer for a problem has a 'Fix it' link that goes to its screen and closes the drawer", async () => {
    events = [event({ id: "ev-bad", category: "download.handoff.failed", label: "Hand-off failed", message: "The hand-off failed", severity: "warning", outcome: "problem" })];
    renderActivity();

    await userEvent.click(await screen.findByText("The hand-off failed"));
    const link = within(screen.getByRole("dialog")).getByRole("link", { name: /In progress/ });
    expect(link).toHaveAttribute("href", "/queue");
    await userEvent.click(link);
    expect(screen.getByLabelText("address").textContent).toBe("/queue");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });
});

describe("Activity's history list (#1329)", () => {
  it("an event whose outcome is a problem wears the 'Went wrong' pill and an ordinary one 'Done'", async () => {
    events = [
      event({ id: "ev-bad", message: "It went badly", outcome: "problem" }),
      event({ id: "ev-ok", message: "It went well", outcome: "good" })
    ];
    renderActivity();

    const bad = (await screen.findByText("It went badly")).closest("[role=row]") as HTMLElement;
    const ok = screen.getByText("It went well").closest("[role=row]") as HTMLElement;
    expect(within(bad).getByText("Went wrong")).toBeInTheDocument();
    expect(within(ok).queryByText("Went wrong")).not.toBeInTheDocument();
    expect(within(ok).getByText("Done")).toBeInTheDocument();
  });

  it("clicking the Title heading asks the server for that sort and the open rows re-order", async () => {
    loaderData.jobs = [
      job({ id: "j-b", label: "Search for Bravo", status: "running", startedUtc: "2026-09-27T12:00:00Z" }),
      job({ id: "j-a", label: "Search for Alpha", status: "running", startedUtc: "2026-09-27T11:00:00Z" })
    ];
    events = [event({ id: "ev-1" })];
    renderActivity();
    await screen.findByText("Message of ev-1");
    expect(activityCalls().every((path) => !path.includes("sortBy"))).toBe(true);

    await userEvent.click(screen.getByRole("button", { name: "Title" }));
    await waitFor(() => expect(activityCalls().some((path) => /sortBy=title&sortDir=asc/.test(path))).toBe(true));
    const titles = () => screen.getAllByRole("row").slice(1).map((row) => row.textContent ?? "");
    await waitFor(() => expect(titles().findIndex((text) => text.includes("Alpha"))).toBeLessThan(titles().findIndex((text) => text.includes("Bravo"))));

    await userEvent.click(screen.getByRole("button", { name: "What happened" }));
    await waitFor(() => expect(activityCalls().some((path) => /sortBy=what&sortDir=asc/.test(path))).toBe(true));
  });

  it("an event address that is never found, with the history exhausted, opens nothing and does not keep paging", async () => {
    events = [event({ id: "ev-1" })];
    renderActivity("/queue?view=history&event=missing");

    await screen.findByText("Message of ev-1");
    await waitFor(() => expect(activityCalls()).toHaveLength(1));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    // No more asking follows.
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 150)); }); // reason: let a runaway loop of page reads show itself
    expect(activityCalls()).toHaveLength(1);
  });

  it("a pushed event the view cannot settle, or a view sorted by a heading, is read again once after a burst", async () => {
    events = [event({ id: "ev-1" })];
    renderActivity("/queue?view=history&title=Arrival");
    await screen.findByText("Message of ev-1");
    expect(activityCalls()).toHaveLength(2 - 1);

    // The push carries no title, so a title-filtered view cannot tell if it belongs: three pushes, one read.
    const push = live.handlers.get("ActivityEventAdded")!;
    act(() => {
      for (const id of ["p1", "p2", "p3"]) push({ id, category: "x", message: id, severity: "info", createdUtc: "2026-09-27T10:00:00Z" });
    });
    await waitFor(() => expect(activityCalls()).toHaveLength(2), { timeout: 4000 });
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 1300)); }); // reason: a second read inside the coalescing window would show here
    expect(activityCalls()).toHaveLength(2);
  });

  it("a pushed event on a view sorted by a heading is read again once after a burst, not put at the head", async () => {
    events = [event({ id: "ev-1" })];
    renderActivity();
    await screen.findByText("Message of ev-1");
    await userEvent.click(screen.getByRole("button", { name: "Title" }));
    await waitFor(() => expect(activityCalls().some((path) => /sortBy=title&sortDir=asc/.test(path))).toBe(true));
    const before = activityCalls().length;

    const push = live.handlers.get("ActivityEventAdded")!;
    act(() => {
      for (const id of ["p1", "p2", "p3"]) push({ id, category: "x", message: id, severity: "info", createdUtc: "2026-09-27T10:00:00Z" });
    });
    await waitFor(() => expect(activityCalls()).toHaveLength(before + 1), { timeout: 4000 });
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 1300)); }); // reason: a second read inside the coalescing window would show here
    expect(activityCalls()).toHaveLength(before + 1);
    expect(screen.queryByText("p1")).not.toBeInTheDocument();
  });

  it("a routine job's folded row says '+1 more step for this run - open to see them'", async () => {
    events = [
      event({ id: "job-completed", category: "job.completed", label: undefined, message: "Read your files (Movies)", relatedJobId: "job-42", createdUtc: "2026-09-27T08:05:00Z" }),
      event({ id: "job-started", category: "job.started", label: undefined, message: "Started reading your files (Movies)", relatedJobId: "job-42", createdUtc: "2026-09-27T08:04:00Z" })
    ];
    renderActivity();

    await screen.findByText("Read your files (Movies)");
    expect(screen.getByText(/\+1 more step for this run/).textContent).toContain("open to see them");
    expect(screen.queryByText("Started reading your files (Movies)")).not.toBeInTheDocument();
  });

  it("the pager reads 'Loading' and is disabled while the next page is fetched", async () => {
    let release: (value: unknown) => void = () => undefined;
    override = (path) => {
      if (!path.startsWith("/api/activity?")) return undefined;
      if (path.includes("pageToken=t2")) return new Promise((resolve) => { release = resolve; });
      return jsonResponse({ items: [event({ id: "ev-1" })], nextPageToken: "t2", hasMore: true });
    };
    renderActivity();

    await userEvent.click(await screen.findByRole("button", { name: "Show older events" }));
    const pager = await screen.findByRole("button", { name: "Loading" });
    expect(pager).toBeDisabled();
    release(jsonResponse({ items: [event({ id: "ev-2" })], nextPageToken: null, hasMore: false }));
    expect(await screen.findByRole("button", { name: "Show older events" }).catch(() => null)).toBeNull();
    expect(await screen.findByText("Message of ev-2")).toBeInTheDocument();
  });

  it("a history that is still being read says 'Reading the history' and 'This takes a moment'", async () => {
    override = (path) => (path.startsWith("/api/activity?") ? new Promise(() => undefined) : undefined);
    renderActivity();

    expect(await screen.findByText("Reading the history")).toBeInTheDocument();
    expect(screen.getByText("This takes a moment.")).toBeInTheDocument();
  });

  it("when the next page fails to read while rows are showing, a banner under the table says so and Try again reads it", async () => {
    let failNext = true;
    override = (path) => {
      if (!path.startsWith("/api/activity?")) return undefined;
      if (path.includes("pageToken=t2")) return failNext ? failure(400) : jsonResponse({ items: [event({ id: "ev-2" })], nextPageToken: null, hasMore: false });
      return jsonResponse({ items: [event({ id: "ev-1" })], nextPageToken: "t2", hasMore: true });
    };
    renderActivity();

    await userEvent.click(await screen.findByRole("button", { name: "Show older events" }));
    expect(await screen.findByText(/The history could not be read just now\./)).toBeInTheDocument();
    expect(screen.getByText("Message of ev-1")).toBeInTheDocument();
    failNext = false;
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    await waitFor(() => expect(screen.queryByText(/The history could not be read just now./)).not.toBeInTheDocument());
    await userEvent.click(await screen.findByRole("button", { name: "Show older events" }));
    expect(await screen.findByText("Message of ev-2")).toBeInTheDocument();
  });
});

describe("Activity's open items (#1329)", () => {
  it("a job that will try again reads 'Retrying, attempt 2 of 3' under In progress and is not under Failed", async () => {
    loaderData.jobs = [job({ id: "job-r", label: "Search for Arrival", status: "failed", attempts: 2, maxAttempts: 3, nextAttemptUtc: "2026-09-27T13:00:00Z", lastError: null })];
    loaderData.failedJobs = [job({ id: "job-r", label: "Search for Arrival", status: "failed", attempts: 2, maxAttempts: 3, nextAttemptUtc: "2026-09-27T13:00:00Z", lastError: null })];
    renderActivity("/queue?view=history&status=in-progress");

    expect(await screen.findByText("Retrying, attempt 2 of 3")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("radio", { name: /^Failed/ }));
    expect(screen.queryByText("Search for Arrival")).not.toBeInTheDocument();
  });

  it("a stopped job with no recorded error reads 'Gave up after 3 attempts' (and '1 attempt' for one)", async () => {
    loaderData.failedJobs = [
      stopped({ id: "job-n3", label: "Import Arrival", lastError: null, attempts: 3 }),
      stopped({ id: "job-n1", label: "Import Sintel", lastError: null, attempts: 1 })
    ];
    renderActivity("/queue?view=history&status=failed");

    expect(await screen.findByText("Gave up after 3 attempts")).toBeInTheDocument();
    expect(screen.getByText("Gave up after 1 attempt")).toBeInTheDocument();
  });

  it("a TV import case is labelled TV and Retry posts to the series route", async () => {
    (loaderData.seriesRecovery as unknown as { recentCases: unknown[]; openCount: number }).openCount = 1;
    (loaderData.seriesRecovery as unknown as { recentCases: unknown[] }).recentCases = [{
      id: "tv-case", title: "Some Show", failureKind: "processedFileIsLess", summary: "The processed file is smaller.", recommendedAction: "Try the import again.",
      detailsJson: JSON.stringify({ DispatchId: "d1" }), detectedUtc: "2026-09-27T08:00:00Z"
    }];
    renderActivity("/queue?view=history&status=needs-decision");

    await userEvent.click(await screen.findByText("Some Show"));
    const dialog = screen.getByRole("dialog");
    expect(dialog.textContent).toContain("TV");
    await userEvent.click(within(dialog).getByRole("button", { name: "Retry: Some Show" }));
    await waitFor(() => expect(authedFetchMock.mock.calls.some((call) => call[0] === "/api/series/import-recovery/tv-case/retry")).toBe(true));
    expect(authedFetchMock.mock.calls.some((call) => String(call[0]).startsWith("/api/movies/import-recovery"))).toBe(false);
  });

  it("finished sends (Imported, Refused, Removed) show under All but not under Done, In progress or Failed", async () => {
    loaderData.dispatches = [
      dispatch({ id: "d-ok", releaseName: "Imported.Film.2020", status: "imported", importStatus: "completed", importedFilePath: "D:/Films/Imported Film.mkv", importCompletedUtc: "2026-09-27T09:00:00Z" }),
      dispatch({ id: "d-refused", releaseName: "Refused.Film.2019", grabStatus: "refused", grabMessage: "The client turned it down." }),
      dispatch({ id: "d-removed", releaseName: "Removed.Film.2018", status: "removed" })
    ];
    events = [event({ id: "ev-1" })];
    renderActivity();

    await screen.findByText("Message of ev-1");
    for (const name of ["Refused.Film.2019", "Removed.Film.2018"]) expect(screen.getByText(name)).toBeInTheDocument();
    for (const chip of [/^Done/, /^In progress/, /^Failed/]) {
      await userEvent.click(screen.getByRole("radio", { name: chip }));
      for (const name of ["Refused.Film.2019", "Removed.Film.2018"]) expect(screen.queryByText(name)).not.toBeInTheDocument();
    }
  });

  it("the filters narrow the open items by title, one item and day, and a history-only filter leaves none", async () => {
    loaderData.jobs = [
      job({ id: "j-arrival", label: "Search for Arrival", status: "running", startedUtc: "2026-09-27T12:00:00Z", scheduledUtc: "2026-09-27T12:00:00Z", relatedEntityId: "movie-1" }),
      job({ id: "j-sintel", label: "Search for Sintel", status: "running", startedUtc: "2026-10-05T12:00:00Z", scheduledUtc: "2026-10-05T12:00:00Z", relatedEntityId: "movie-2" })
    ];
    events = [event({ id: "ev-1" })];
    const open = async (address: string) => {
      const view = renderActivity(address);
      await screen.findByText("Message of ev-1");
      return view;
    };

    let view = await open("/queue?view=history&title=Sintel");
    expect(screen.queryByText("Search for Arrival")).not.toBeInTheDocument();
    expect(screen.getByText("Search for Sintel")).toBeInTheDocument();
    view.unmount();

    view = await open("/queue?view=history&item=movie-1&itemType=movie");
    expect(screen.getByText("Search for Arrival")).toBeInTheDocument();
    expect(screen.queryByText("Search for Sintel")).not.toBeInTheDocument();
    view.unmount();

    view = await open("/queue?view=history&since=2026-10-01");
    expect(screen.queryByText("Search for Arrival")).not.toBeInTheDocument();
    expect(screen.getByText("Search for Sintel")).toBeInTheDocument();
    view.unmount();

    view = await open("/queue?view=history&until=2026-10-01");
    expect(screen.getByText("Search for Arrival")).toBeInTheDocument();
    expect(screen.queryByText("Search for Sintel")).not.toBeInTheDocument();
    view.unmount();

    for (const query of ["type=search", "file=a.mkv", "source=indexer", "outcome=problem", "reason=language"]) {
      view = await open(`/queue?view=history&${query}`);
      expect(screen.queryByText("Search for Arrival")).not.toBeInTheDocument();
      expect(screen.queryByText("Search for Sintel")).not.toBeInTheDocument();
      view.unmount();
    }
  });
});

describe("Activity's live steps (#1329)", () => {
  const handoffs = [
    { id: "h1", libraryId: "l1", mediaType: "movie", clientId: "c1", queueItemId: "q1", releaseName: "Sintel.2010.1080p", sourcePath: "D:/Downloads/Sintel", processorName: "Weir", status: "started", outputPath: null, importJobId: null, failureMessage: null, createdUtc: "2026-10-02T08:00:00Z", updatedUtc: "2026-10-02T08:05:00Z" },
    { id: "h2", libraryId: "l1", mediaType: "movie", clientId: "c1", queueItemId: "q2", releaseName: "Arrival.2016.2160p", sourcePath: "D:/Downloads/Arrival", processorName: "Weir", status: "completed", outputPath: "D:/Clean/Arrival.mkv", importJobId: null, failureMessage: null, createdUtc: "2026-10-05T07:00:00Z", updatedUtc: "2026-10-05T07:10:00Z" }
  ];
  const handoffCalls = () => authedFetchMock.mock.calls.map((call) => String(call[0])).filter((path) => path.startsWith("/api/integrations/processors/handoffs"));

  it("the title, since and until filters narrow the Processing rows", async () => {
    override = (path) => (path.startsWith("/api/integrations/processors/handoffs") ? jsonResponse(handoffs) : undefined);

    let view = renderActivity("/queue?view=history&type=processing&title=Sintel");
    expect(await screen.findByText("Sintel.2010.1080p")).toBeInTheDocument();
    expect(screen.queryByText("Arrival.2016.2160p")).not.toBeInTheDocument();
    view.unmount();

    view = renderActivity("/queue?view=history&type=processing&since=2026-10-04");
    expect(await screen.findByText("Arrival.2016.2160p")).toBeInTheDocument();
    expect(screen.queryByText("Sintel.2010.1080p")).not.toBeInTheDocument();
    view.unmount();

    view = renderActivity("/queue?view=history&type=processing&until=2026-10-03");
    expect(await screen.findByText("Sintel.2010.1080p")).toBeInTheDocument();
    expect(screen.queryByText("Arrival.2016.2160p")).not.toBeInTheDocument();
    view.unmount();
  });

  it("a live step shows a Done count on its chip, and the Processing step reads a 404 as an empty list", async () => {
    override = (path) => (path.startsWith("/api/integrations/processors/handoffs") ? jsonResponse(handoffs) : undefined);
    const view = renderActivity("/queue?view=history&type=processing");
    expect(await screen.findByText("Sintel.2010.1080p")).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Done 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "In progress 1" })).toBeInTheDocument();
    view.unmount();
    queryClient.clear();

    override = (path) => (path.startsWith("/api/integrations/processors/handoffs") ? failure(404) : undefined);
    renderActivity("/queue?view=history&type=processing");
    expect(await screen.findByText("Nothing matches these filters")).toBeInTheDocument();
    expect(screen.queryByText("The history could not be read")).not.toBeInTheDocument();
  });

  it("asks for the three steps only while each is chosen", async () => {
    renderActivity();
    await waitFor(() => expect(activityCalls().length).toBeGreaterThan(0));
    const asked = () => authedFetchMock.mock.calls.map((call) => String(call[0]));
    expect(asked().some((path) => path.startsWith("/api/integrations/processors/handoffs"))).toBe(false);
    expect(asked().some((path) => path.startsWith("/api/download-health"))).toBe(false);
    expect(asked().some((path) => path.startsWith("/api/download-clients/telemetry"))).toBe(false);
    expect(handoffCalls()).toHaveLength(0);
  });
});

describe("Activity decides an import case from its drawer (#1329)", () => {
  const faultCase = () => {
    (loaderData.movieRecovery as unknown as { recentCases: unknown[]; openCount: number }).openCount = 1;
    (loaderData.movieRecovery as unknown as { recentCases: unknown[] }).recentCases = [{
      id: "case-7", title: "Tangerine", failureKind: "processedFileIsLess", summary: "The processed file is smaller.", recommendedAction: "Choose what to do.",
      detailsJson: JSON.stringify({ DispatchId: "d1" }), detectedUtc: "2026-09-27T08:00:00Z"
    }];
  };
  const decide = async (button: string) => {
    renderActivity("/queue?view=history&status=needs-decision");
    await userEvent.click(await screen.findByText("Tangerine"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: button }));
  };

  it("Import original posts import-original, says the title will be imported unprocessed, and closes the drawer", async () => {
    faultCase();
    await decide("Import original: Tangerine");

    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Tangerine will be imported unprocessed."));
    expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/case-7/import-original", expect.objectContaining({ method: "POST" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("Search again posts search-again, says it is searching for another release, and blocklists nothing", async () => {
    faultCase();
    await decide("Search again: Tangerine");

    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Searching for another release for Tangerine."));
    const paths = authedFetchMock.mock.calls.map((call) => String(call[0]));
    expect(paths).toContain("/api/movies/import-recovery/case-7/search-again");
    expect(paths.some((path) => /blocklist|block-list/i.test(path))).toBe(false);
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("a decision the server answers 409 with a message shows the server's words, reads the list again and keeps the drawer", async () => {
    faultCase();
    override = (path) => (path.endsWith("/import-original")
      ? { ok: false, status: 409, json: async () => ({ message: "That import was already finished." }), text: async () => JSON.stringify({ message: "That import was already finished." }) }
      : undefined);
    await decide("Import original: Tangerine");

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("That import was already finished."));
    expect(toasts.success).not.toHaveBeenCalled();
    expect(revalidateMock).toHaveBeenCalled();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("clicking a File row in an event's drawer goes to that file's history and closes the drawer", async () => {
    events = [event({ id: "ev-file", message: "Imported the file", title: "Arrival", filePath: "C:/Films/Arrival.mkv" })];
    renderActivity();

    await userEvent.click(await screen.findByText("Imported the file"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("link", { name: "C:/Films/Arrival.mkv" }));

    expect(screen.getByLabelText("address").textContent).toBe(`/queue?view=history&file=${encodeURIComponent("C:/Films/Arrival.mkv")}`);
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });
});
