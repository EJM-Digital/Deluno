import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterAll, beforeEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventItem, MovieImportRecoverySummary, SeriesImportRecoverySummary } from "../lib/api";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const activityEvents = [
    {
      id: "event-1",
      category: "release.blocked",
      label: "Release blocked",
      message: 'Deluno will not use Arrival.2016.2160p again.',
      severity: "info",
      detail: undefined,
      detailsJson: null,
      relatedJobId: null,
      relatedEntityType: "movie",
      relatedEntityId: "movie-1",
      createdUtc: "2026-09-27T10:00:00Z"
    },
    {
      id: "event-2",
      category: "filesystem.import.completed",
      label: "Imported into the library",
      message: "Imported Arrival (2016).",
      severity: "success",
      detail: undefined,
      detailsJson: null,
      relatedJobId: null,
      relatedEntityType: "movie",
      relatedEntityId: "movie-1",
      createdUtc: "2026-09-27T09:00:00Z"
    },
    {
      id: "job-completed",
      category: "job.completed",
      message: "Every file Deluno holds has been read and evaluated under its current preference plan. (Movies)",
      severity: "info",
      detail: undefined,
      detailsJson: null,
      relatedJobId: "job-42",
      relatedEntityType: "movie",
      relatedEntityId: null,
      createdUtc: "2026-09-27T08:05:00Z"
    },
    {
      id: "job-started",
      category: "job.started",
      message: "Started reading what your files actually are (Movies).",
      severity: "info",
      detail: undefined,
      detailsJson: null,
      relatedJobId: "job-42",
      relatedEntityType: "movie",
      relatedEntityId: null,
      createdUtc: "2026-09-27T08:04:00Z"
    },
    {
      id: "job-failed",
      category: "job.failed",
      label: "Task failed",
      message: "A saved key can't be read on this computer. Enter it again on the connection's page. Deluno will try again in about 5 minutes.",
      severity: "warning",
      detail: undefined,
      detailsJson: null,
      relatedJobId: "job-43",
      relatedEntityType: "movie",
      relatedEntityId: null,
      createdUtc: "2026-09-27T08:02:00Z"
    },
    {
      id: "job-queued",
      category: "job.queued",
      message: "Added a media read (Movies) to the queue.",
      severity: "info",
      detail: undefined,
      detailsJson: null,
      relatedJobId: "job-42",
      relatedEntityType: "movie",
      relatedEntityId: null,
      createdUtc: "2026-09-27T08:03:00Z"
    },
    {
      id: "season-pack-completed",
      category: "filesystem.import.season-pack.completed",
      message: "Some Show season pack imported 9 season-pack files covering 9 episodes.",
      severity: "success",
      detail: undefined,
      detailsJson: JSON.stringify({
        Files: [],
        Skipped: [
          { SourcePath: "C:/Downloads/Some.Show.S01/Some.Show.S01.Extras.Making.Of.mkv", Reason: "The filename does not resolve to one or more canonical episodes in this show." },
          { SourcePath: "C:/Downloads/Some.Show.S01/Some.Show.S01E05.mkv", Reason: "Matched to episode(s) S01E05 but not imported: No video stream was detected." }
        ]
      }),
      relatedJobId: null,
      relatedEntityType: "series",
      relatedEntityId: "series-1",
      createdUtc: "2026-09-27T11:00:00Z"
    }
] as ActivityEventItem[];

const loaderData = {
  dispatches: [],
  failedJobs: [],
  jobs: [],
  movieRecovery: {
    openCount: 1,
    qualityCount: 0,
    unmatchedCount: 0,
    corruptCount: 0,
    downloadFailedCount: 0,
    importFailedCount: 0,
    recentCases: [
      {
        id: "case-1",
        title: "Arrival",
        failureKind: "noVideoStream",
        summary: "No video stream was detected in this file.",
        recommendedAction: "Choose a valid video file. Subtitle, sample, archive, or metadata-only files should not be imported.",
        detailsJson: '{"sourcePath":"C:/Downloads/Arrival.mkv"}',
        detectedUtc: "2026-09-27T08:00:00Z"
      }
    ]
  } as MovieImportRecoverySummary,
  seriesRecovery: {
    openCount: 0,
    qualityCount: 0,
    unmatchedCount: 0,
    corruptCount: 0,
    downloadFailedCount: 0,
    importFailedCount: 0,
    recentCases: []
  } as SeriesImportRecoverySummary
};

const revalidateMock = vi.fn();

const filterChoices = {
  types: [{ value: "search", label: "Searched" }, { value: "turned-down", label: "Turned down" }, { value: "blocklisted", label: "Blocklisted" }],
  sources: [{ value: "indexer", label: "An indexer" }, { value: "client", label: "A download client" }],
  outcomes: [{ value: "good", label: "Went well" }, { value: "problem", label: "Went wrong" }],
  reasons: [{ value: "language", label: "Not in a language you want" }, { value: "tooBig", label: "Bigger than your size limit" }]
};

const jsonResponse = (body: unknown) => ({ ok: true, status: 200, json: async () => body });

/** What the server says to the history: the filter words, and a page of events (narrowed to the blocklist when asked). */
function answerActivityApi(path: string) {
  if (path.startsWith("/api/activity/filters")) return jsonResponse(filterChoices);
  if (path.startsWith("/api/activity?")) {
    const params = new URLSearchParams(path.split("?")[1]);
    const items = params.get("type") === "blocklisted" ? activityEvents.filter((event) => event.category.includes("block")) : activityEvents;
    return jsonResponse({ items, nextPageToken: null, hasMore: false });
  }
  return jsonResponse({});
}

function installActivityApi() {
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation(async (path: string) => answerActivityApi(path));
}

beforeEach(() => installActivityApi());

const requestedActivityPaths = () => authedFetchMock.mock.calls.map((call) => String(call[0])).filter((path) => path.startsWith("/api/activity?"));

function LocationProbe() {
  const location = useLocation();
  return <output aria-label="address">{location.pathname + location.search}</output>;
}

/** The history is part of the page's opening view (All); wait for its first rows to arrive. */
async function openHistory() {
  await screen.findByText("Imported Arrival (2016).");
}

function renderActivity(address = "/activity") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[address]}>
        <ActivityPage />
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>
  );
}
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
}));

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(loaderData, revalidateMock)
  };
});

const { ActivityPage } = await import("./activity-page");

/**
 * #798: an event-type filter that actually narrows the list, and a rejected
 * import's specific reason surfaced instead of a generic label.
 */
describe("Activity history filters and columns (#798, #1100)", () => {
  it("asks the server for the chosen kind of event and keeps the choice in the address", async () => {
    renderActivity();
    await openHistory();

    expect(screen.getByText("Deluno will not use Arrival.2016.2160p again.")).toBeInTheDocument();
    expect(screen.getByText("Imported Arrival (2016).")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("combobox", { name: "Filter by event type" }));
    await userEvent.click(await screen.findByRole("option", { name: "Blocklisted" }));

    await waitFor(() => expect(screen.queryByText("Imported Arrival (2016).")).not.toBeInTheDocument());
    expect(screen.getByText("Deluno will not use Arrival.2016.2160p again.")).toBeInTheDocument();
    expect(requestedActivityPaths().some((path) => path.includes("type=blocklisted"))).toBe(true);
    expect(screen.getByLabelText("address").textContent).toContain("type=blocklisted");
  });

});

/**
 * #1100: the history is one page and every filter is in the address, so a view is a link: Live's
 * Turned down card, a title's page and a file's page open it, and what a person narrows to is
 * what the link they copy says.
 */
describe("Activity history keeps its filters in the address (#1100)", () => {
  it("reads the filters from the address and asks the server for exactly those", async () => {
    window.localStorage.clear();
    renderActivity("/activity?type=turned-down&since=today&reason=language&title=Sintel&file=Sintel.mkv&source=indexer&outcome=good&until=2099-12-31");

    await waitFor(() => expect(requestedActivityPaths().length).toBeGreaterThan(0));
    const asked = new URLSearchParams(requestedActivityPaths()[0].split("?")[1]);
    expect(asked.get("type")).toBe("turned-down");
    expect(asked.get("reason")).toBe("language");
    expect(asked.get("title")).toBe("Sintel");
    expect(asked.get("file")).toBe("Sintel.mkv");
    expect(asked.get("source")).toBe("indexer");
    expect(asked.get("outcome")).toBe("good");
    // "today" is the start of the person's own day, sent as a moment the server can compare.
    const now = new Date();
    expect(new Date(asked.get("since")!).getTime()).toBe(new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime());
    expect(asked.get("until")).toBe(new Date(2100, 0, 1).toISOString());
    // The reason menu is there because turned-down releases are being looked at.
    expect(screen.getByRole("combobox", { name: "Filter by reason" })).toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "Filter by title" })).toHaveValue("Sintel");
    expect(screen.getByRole("searchbox", { name: "Filter by file" })).toHaveValue("Sintel.mkv");
  });

  it("writes a typed title to the address after a moment and clears every filter in one press", async () => {
    renderActivity("/activity?status=done&type=search");
    await screen.findByText("Imported Arrival (2016).");

    await userEvent.type(screen.getByRole("searchbox", { name: "Filter by title" }), "Arrival");
    await waitFor(() => expect(screen.getByLabelText("address").textContent).toContain("title=Arrival"));
    expect(screen.getByLabelText("address").textContent).toContain("type=search");

    await userEvent.click(screen.getAllByRole("button", { name: "Clear filters" })[0]);
    await waitFor(() => expect(screen.getByLabelText("address").textContent).not.toMatch(/title=|type=/));
  });

  it("limits to one title from its page link and lets a person step out of it", async () => {
    renderActivity("/activity?title=Arrival&item=movie-1&itemType=movie");

    await screen.findByText("Imported Arrival (2016).");
    const asked = new URLSearchParams(requestedActivityPaths()[0].split("?")[1]);
    expect(asked.get("relatedEntityId")).toBe("movie-1");
    expect(asked.get("relatedEntityType")).toBe("movie");
    expect(asked.get("title")).toBe("Arrival");

    await userEvent.click(screen.getByRole("button", { name: "Only this title: stop" }));
    await waitFor(() => expect(screen.getByLabelText("address").textContent).not.toContain("item="));
  });

  it("says nothing matches, rather than that nothing has happened, when a filter finds no events", async () => {
    authedFetchMock.mockImplementation(async (path: string) =>
      path.startsWith("/api/activity?") ? jsonResponse({ items: [], nextPageToken: null, hasMore: false }) : answerActivityApi(path));
    renderActivity("/activity?type=failed");

    expect(await screen.findByText("Nothing matches these filters")).toBeInTheDocument();
  });

  it("shows a turned-down release with its title, the indexer and the reason in plain words", async () => {
    const turnedDown = {
      id: "td-1", category: "release.turned-down", label: "Release turned down", message: "Turned down Sintel.2010.GERMAN.1080p · Not in a language you want",
      detailsJson: null, relatedJobId: null, relatedEntityType: "movie", relatedEntityId: "m1", createdUtc: "2026-09-27T12:00:00Z",
      title: "Sintel", sourceName: "NZBgeek", reasonGroup: "language", type: "turned-down", source: "indexer", outcome: "good"
    } as ActivityEventItem;
    authedFetchMock.mockImplementation(async (path: string) =>
      path.startsWith("/api/activity?") ? jsonResponse({ items: [turnedDown], nextPageToken: null, hasMore: false }) : answerActivityApi(path));
    renderActivity("/activity?type=turned-down");

    await userEvent.click(await screen.findByText("Turned down Sintel.2010.GERMAN.1080p · Not in a language you want"));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("Sintel")).toBeInTheDocument();
    expect(within(dialog).getByText("NZBgeek")).toBeInTheDocument();
    expect(within(dialog).getByText("Not in a language you want")).toBeInTheDocument();
    // From the drawer, everything about that title is one link away.
    expect(within(dialog).getByRole("link", { name: "Everything about Sintel" })).toHaveAttribute("href", expect.stringContaining("item=m1"));
    expect(document.body.textContent).not.toMatch(/release\.turned-down|notBetter|reasonGroup/);
  });

  it("opens the event a link names (the bell's rows), reading older pages until it is found", async () => {
    authedFetchMock.mockImplementation(async (path: string) => {
      if (!path.startsWith("/api/activity?")) return answerActivityApi(path);
      return path.includes("pageToken=next")
        ? jsonResponse({ items: [activityEvents[1]], nextPageToken: null, hasMore: false })
        : jsonResponse({ items: [activityEvents[0]], nextPageToken: "next", hasMore: true });
    });
    renderActivity("/activity?event=event-2&since=2026-09-27&until=2026-09-27");

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getAllByText("Imported Arrival (2016).").length).toBeGreaterThan(0);
  });

  it("shows older events a page at a time", async () => {
    authedFetchMock.mockImplementation(async (path: string) => {
      if (!path.startsWith("/api/activity?")) return answerActivityApi(path);
      return path.includes("pageToken=next")
        ? jsonResponse({ items: [activityEvents[1]], nextPageToken: null, hasMore: false })
        : jsonResponse({ items: [activityEvents[0]], nextPageToken: "next", hasMore: true });
    });
    renderActivity("/activity?status=done");

    await screen.findByText("Deluno will not use Arrival.2016.2160p again.");
    expect(screen.queryByText("Imported Arrival (2016).")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Show older events" }));

    expect(await screen.findByText("Imported Arrival (2016).")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Show older events" })).not.toBeInTheDocument();
  });
});

describe("Activity names each kind of event in plain words (#1086)", () => {
  it("shows the label in the list and the drawer, never the internal code", async () => {
    // An earlier test hides this column and the choice is remembered.
    window.localStorage.clear();
    renderActivity();
    await openHistory();

    expect(screen.getByText("Imported into the library")).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/filesystem\.import\.completed|release\.blocked/);

    await userEvent.click(screen.getByText("Imported Arrival (2016)."));
    expect(await screen.findByText(/Imported into the library ·/)).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/filesystem\.import\.completed/);
  });
});

describe("Activity shows failures and retry times in plain words (#1095)", () => {
  it("shows the plain reason and a relative retry time, and no machine timestamp anywhere", async () => {
    window.localStorage.clear();
    renderActivity();
    await openHistory();

    expect(screen.getByText(/Deluno will try again in about 5 minutes\./)).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/);
    expect(document.body.textContent).not.toMatch(/Key not valid|will retry after/);
  });
});

describe("Import issues show the specific rejection reason (#798)", () => {
  it("names the actual failure in plain words rather than a generic label", async () => {
    renderActivity("/activity?status=needs-decision");

    expect(screen.getByText("Arrival")).toBeInTheDocument();
    expect(screen.getByText("No video in the file")).toBeInTheDocument();
    expect(screen.getByText("No video stream was detected in this file.")).toBeInTheDocument();
  });

  it("opens the full reason, recommendation and evidence in a drawer", async () => {
    renderActivity("/activity?status=needs-decision");
    await userEvent.click(screen.getByText("Arrival"));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("No video stream was detected in this file.")).toBeInTheDocument();
    expect(within(dialog).getByText(/Choose a valid video file/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Arrival\.mkv/)).toBeInTheDocument();
  });
});

describe("A routine job's queued/started/completed fold into one row (#916)", () => {
  it("shows one row for the job run, not three", async () => {
    renderActivity();
    await openHistory();

    expect(screen.getByText("Every file Deluno holds has been read and evaluated under its current preference plan. (Movies)")).toBeInTheDocument();
    expect(screen.queryByText("Started reading what your files actually are (Movies).")).not.toBeInTheDocument();
    expect(screen.queryByText("Added a media read (Movies) to the queue.")).not.toBeInTheDocument();
  });

  it("opens the queued and started steps in the drawer", async () => {
    renderActivity();
    await openHistory();
    await userEvent.click(screen.getByText("Every file Deluno holds has been read and evaluated under its current preference plan. (Movies)"));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("Started reading what your files actually are (Movies).")).toBeInTheDocument();
    expect(within(dialog).getByText("Added a media read (Movies) to the queue.")).toBeInTheDocument();
  });
});

/**
 * #889: a season pack that only partly matched names, in plain words, what
 * did not go in - and a person can confirm deleting exactly those files
 * from where the import left them.
 */
describe("A season pack's leftovers can be reviewed and deleted (#889)", () => {
  beforeEach(() => {
    installActivityApi();
    revalidateMock.mockReset();
  });

  it("lists what did not match, in plain words, with the file names", async () => {
    renderActivity();
    await openHistory();
    await userEvent.click(screen.getByText("Some Show season pack imported 9 season-pack files covering 9 episodes."));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("What didn't match")).toBeInTheDocument();
    expect(within(dialog).getByText("Some.Show.S01.Extras.Making.Of.mkv")).toBeInTheDocument();
    expect(within(dialog).getByText("The filename does not resolve to one or more canonical episodes in this show.")).toBeInTheDocument();
    expect(within(dialog).getByText("Some.Show.S01E05.mkv")).toBeInTheDocument();
    expect(within(dialog).getAllByText(/Matched to episode\(s\) S01E05 but not imported/).length).toBeGreaterThan(0);
  });

  it("confirms, names the files, deletes them, and clears the problem from the screen", async () => {
    authedFetchMock.mockImplementation(async (path: string) =>
      path.startsWith("/api/filesystem/") ? jsonResponse({ deleted: 2, refused: [] }) : answerActivityApi(path));

    renderActivity();
    await openHistory();
    await userEvent.click(screen.getByText("Some Show season pack imported 9 season-pack files covering 9 episodes."));

    await userEvent.click(screen.getByRole("button", { name: "Delete leftover files" }));

    const confirmDialog = screen.getByRole("dialog", { name: "Delete leftover files?" });
    expect(within(confirmDialog).getByText(/Some\.Show\.S01\.Extras\.Making\.Of\.mkv/)).toBeInTheDocument();
    expect(within(confirmDialog).getByText(/Some\.Show\.S01E05\.mkv/)).toBeInTheDocument();

    await userEvent.click(within(confirmDialog).getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith(
      "/api/filesystem/import/season-pack/leftovers/delete",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({
          paths: [
            "C:/Downloads/Some.Show.S01/Some.Show.S01.Extras.Making.Of.mkv",
            "C:/Downloads/Some.Show.S01/Some.Show.S01E05.mkv"
          ]
        })
      })
    ));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

});

describe("Activity names each job in plain words (#1086)", () => {
  it("shows the label in the failed list, the drawer and the toast text, never the job type", async () => {
    const job = {
      id: "job-9", jobType: "episode.search", label: "Search for an episode", source: "queue-derived", sourceLabel: "A download in the queue", status: "dead-letter",
      payloadJson: "{}", attempts: 3, createdUtc: "2026-09-27T08:00:00Z", scheduledUtc: "2026-09-27T08:00:00Z", startedUtc: null,
      completedUtc: "2026-09-27T08:05:00Z", leasedUntilUtc: null, workerId: null, lastError: "The indexer did not answer.",
      relatedEntityType: "movie", relatedEntityId: "movie-1", idempotencyKey: null, dedupeKey: null, maxAttempts: 3, lastAttemptUtc: null, nextAttemptUtc: null
    };
    const saved = { jobs: loaderData.jobs, failedJobs: loaderData.failedJobs };
    loaderData.jobs = [job] as never;
    loaderData.failedJobs = [job] as never;
    try {
      renderActivity("/activity?status=failed");

      await userEvent.click(screen.getAllByText("Search for an episode")[0]);
      expect(document.body.textContent).toContain("Search for an episode");
      expect(document.body.textContent).toContain("A download in the queue");
      expect(document.body.textContent).not.toMatch(/episode\.search|queue-derived/);
    } finally {
      loaderData.jobs = saved.jobs;
      loaderData.failedJobs = saved.failedJobs;
    }
  });
});

/**
 * #1114: Activity is one list with status chips instead of tabs. The open items (title jobs, sends,
 * import issues) come first; the history follows; jobs that are not about a title are not listed.
 */
describe("Activity is one list with status chips (#1114)", () => {
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

  const saved = { jobs: loaderData.jobs, failedJobs: loaderData.failedJobs, dispatches: loaderData.dispatches };
  const restore = () => Object.assign(loaderData, saved);
  const seed = () => {
    loaderData.jobs = [
      job({ id: "job-running", label: "Search for Arrival", status: "running", startedUtc: "2026-09-27T12:00:00Z" }),
      job({ id: "job-library", label: "Refresh all metadata", status: "running", relatedEntityType: null, relatedEntityId: null })
    ] as never;
    loaderData.failedJobs = [
      job({ id: "job-stopped", label: "Import Arrival", status: "dead-letter", attempts: 3, completedUtc: "2026-09-27T09:30:00Z", lastError: "The indexer did not answer." }),
      job({ id: "job-backup", label: "Back up the database", status: "failed", relatedEntityType: null, relatedEntityId: null, lastError: "Disk full." })
    ] as never;
    loaderData.dispatches = [
      dispatch({ id: "dispatch-sent" }),
      dispatch({ id: "dispatch-refused", releaseName: "Sintel.2010.720p", status: "failed", grabMessage: "The client refused the file." })
    ] as never;
  };
  beforeEach(() => { restore(); seed(); });
  afterAll(restore);

  it("shows the status chips with the counts that are known, and no count where it is not", async () => {
    renderActivity();
    await openHistory();

    expect(screen.getByRole("radio", { name: "All" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("radio", { name: "In progress 2" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Failed 2" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Needs a decision 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Done" })).toBeInTheDocument();
    expect(screen.queryByRole("tab")).not.toBeInTheDocument();
  });

  it("lists the open items first, then the history, each with its status pill", async () => {
    renderActivity();
    await openHistory();

    const rows = screen.getAllByRole("row").slice(1);
    const importedAt = rows.findIndex((row) => row.textContent?.includes("Imported Arrival (2016)."));
    const failedAt = rows.findIndex((row) => row.textContent?.includes("Import Arrival") && !row.textContent?.includes("Imported"));
    expect(failedAt).toBeGreaterThanOrEqual(0);
    expect(failedAt).toBeLessThan(importedAt);
    expect(within(rows[importedAt]).getByText("Done")).toBeInTheDocument();
    expect(within(rows[failedAt]).getByText("Failed")).toBeInTheDocument();
  });

  it("keeps a job with no title off the page, and lists the ones about a title", async () => {
    renderActivity();
    await openHistory();

    expect(screen.getByText("Search for Arrival")).toBeInTheDocument();
    expect(screen.getByText("Import Arrival")).toBeInTheDocument();
    expect(screen.queryByText("Refresh all metadata")).not.toBeInTheDocument();
    expect(screen.queryByText("Back up the database")).not.toBeInTheDocument();
  });

  it("lists on each chip what that chip is for", async () => {
    renderActivity("/activity?status=in-progress");
    expect(screen.getByText("Search for Arrival")).toBeInTheDocument();
    expect(screen.getByText("Arrival.2016.1080p")).toBeInTheDocument();
    expect(screen.queryByText("Import Arrival")).not.toBeInTheDocument();
    expect(screen.queryByText("Imported Arrival (2016).")).not.toBeInTheDocument();
    // The history is not even asked for while another chip is chosen.
    expect(requestedActivityPaths()).toHaveLength(0);

    await userEvent.click(screen.getByRole("radio", { name: "Failed 2" }));
    expect(screen.getByText("Import Arrival")).toBeInTheDocument();
    expect(screen.getByText("Sintel.2010.720p")).toBeInTheDocument();
    expect(screen.getByText("The client refused the file.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry failed jobs" })).toBeEnabled();

    await userEvent.click(screen.getByRole("radio", { name: "Needs a decision 1" }));
    expect(screen.getByText("No video in the file")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retry failed jobs" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("radio", { name: "Done" }));
    expect(await screen.findByText("Imported Arrival (2016).")).toBeInTheDocument();
    expect(screen.queryByText("No video in the file")).not.toBeInTheDocument();
  });

  it("says plainly what would appear on a chip that is empty", () => {
    loaderData.jobs = [] as never;
    loaderData.failedJobs = [] as never;
    loaderData.dispatches = [] as never;
    renderActivity("/activity?status=failed");
    expect(screen.getByText("Nothing has failed")).toBeInTheDocument();
    expect(screen.getByText(/needs another go or a look/)).toBeInTheDocument();
  });

  it("keeps the chosen chip in the address, and no chip is All", async () => {
    renderActivity();
    const address = () => screen.getByLabelText("address").textContent;

    await userEvent.click(screen.getByRole("radio", { name: "Failed 2" }));
    expect(address()).toContain("status=failed");
    await userEvent.click(screen.getByRole("radio", { name: "Needs a decision 1" }));
    expect(address()).toContain("status=needs-decision");
    await userEvent.click(screen.getByRole("radio", { name: "All" }));
    expect(address()).not.toContain("status=");
  });

  it("reads the chip from the address, with the history filters alongside it", () => {
    renderActivity("/activity?status=failed&title=Arrival");
    expect(screen.getByRole("radio", { name: "Failed 1" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("searchbox", { name: "Filter by title" })).toHaveValue("Arrival");
    expect(screen.getByText("Import Arrival")).toBeInTheDocument();
    expect(screen.queryByText("Sintel.2010.720p")).not.toBeInTheDocument();
  });

  it.each([
    ["/activity?section=jobs", "In progress 2"],
    ["/activity?section=imports", "Needs a decision 1"],
    ["/activity?section=events", "All"],
    ["/activity?since=today", "All"],
    ["/activity?event=event-2&since=2026-09-27&until=2026-09-27", "All"]
  ])("keeps the old link %s opening the right chip", (address, chip) => {
    renderActivity(address);
    expect(screen.getByRole("radio", { name: chip })).toHaveAttribute("aria-checked", "true");
  });

  it("opens the job, the send to the client, the history event and the import issue each in its own drawer", async () => {
    renderActivity();
    await openHistory();
    const closeDrawer = async () => {
      await userEvent.keyboard("{Escape}");
      await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    };

    await userEvent.click(screen.getByText("Search for Arrival"));
    expect(within(screen.getByRole("dialog")).getByText("What it was for")).toBeInTheDocument();
    await closeDrawer();

    await userEvent.click(screen.getByText("Import Arrival"));
    expect(within(screen.getByRole("dialog")).getByText("Why it failed")).toBeInTheDocument();
    expect(within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss" })).toBeInTheDocument();
    await closeDrawer();

    await userEvent.click(screen.getByText("Sintel.2010.720p"));
    expect(within(screen.getByRole("dialog")).getAllByText(/Sintel\.2010\.720p/).length).toBeGreaterThan(0);
    await closeDrawer();

    await userEvent.click(screen.getByText("No video in the file"));
    expect(within(screen.getByRole("dialog")).getByText("Why this needs a decision")).toBeInTheDocument();
    await closeDrawer();

    await userEvent.click(screen.getByText("Imported Arrival (2016)."));
    expect(within(screen.getByRole("dialog")).getByText("What happened")).toBeInTheDocument();
  });

  it("retries the failed jobs from the Failed chip", async () => {
    authedFetchMock.mockImplementation(async (path: string) =>
      path === "/api/jobs/retry-failed" ? jsonResponse({ retried: 2 }) : answerActivityApi(path));
    renderActivity("/activity?status=failed");
    await userEvent.click(screen.getByRole("button", { name: "Retry failed jobs" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/jobs/retry-failed", expect.objectContaining({ method: "POST" })));
  });
});
