import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from "vitest";
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

    await userEvent.click(screen.getByRole("button", { name: "More filters" }));
    await userEvent.click(await screen.findByRole("radio", { name: "Blocklisted" }));

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
    // The filters with no control of their own show as tokens at the start of the list card.
    expect(await screen.findByText("Reason: Not in a language you want")).toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "Find a title" })).toHaveValue("Sintel");
    expect(screen.getByText("File: Sintel.mkv")).toBeInTheDocument();
  });

  it("shows a link's file filter as a token and clears the filter when it is removed", async () => {
    renderActivity("/activity?status=done&file=Arrival.mkv");
    expect(await screen.findByText("File: Arrival.mkv")).toBeInTheDocument();
    expect(screen.getByLabelText("address").textContent).toContain("file=Arrival.mkv");

    await userEvent.click(screen.getByRole("button", { name: "Remove filter File: Arrival.mkv" }));
    await waitFor(() => expect(screen.getByLabelText("address").textContent).not.toContain("file="));
    expect(screen.queryByText("File: Arrival.mkv")).not.toBeInTheDocument();
    expect(screen.getByLabelText("address").textContent).toContain("status=done");
  });

  it("writes a typed title to the address after a moment and clears every filter in one press", async () => {
    renderActivity("/activity?status=done&type=search&source=indexer");
    await screen.findByText("Imported Arrival (2016).");

    await userEvent.type(screen.getByRole("searchbox", { name: "Find a title" }), "Arrival");
    await waitFor(() => expect(screen.getByLabelText("address").textContent).toContain("title=Arrival"));
    expect(screen.getByLabelText("address").textContent).toContain("type=search");
    expect(await screen.findByText("From: An indexer")).toBeInTheDocument();

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

    await userEvent.click(screen.getByRole("button", { name: "Remove filter Only this title" }));
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

  it("never prints a code on a row: every failure kind is said in words", async () => {
    const cases = loaderData.movieRecovery.recentCases;
    const kinds = ["downloadFailed", "processor-held", "vanished-downloading", "someNewKind", "another-new-kind"];
    kinds.forEach((kind, index) => cases.push({ ...cases[0], id: `kind-${index}`, title: `Film ${index}`, failureKind: kind }));
    try {
      renderActivity("/activity?status=needs-decision");
      expect(screen.getByText("Download failed")).toBeInTheDocument();
      expect(screen.getByText("Held by the processor")).toBeInTheDocument();
      const rows = screen.getAllByRole("row").map((row) => row.textContent ?? "");
      for (const kind of kinds) expect(rows.join(" ")).not.toContain(kind);
      // No camelCase or kebab-case word is printed in the Title column's second line.
      for (const row of screen.getAllByRole("row")) {
        const step = row.querySelector("[role=cell] span:nth-of-type(2)")?.textContent ?? "";
        expect(step).not.toMatch(/[a-z][A-Z]|[a-z]-[a-z]/);
      }
    } finally {
      cases.splice(1);
    }
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

/** #1114: an import case is decided where it is seen, with the same buttons and words the bell offers. */
describe("An import case is decided from its drawer (#1114)", () => {
  const cases = loaderData.movieRecovery.recentCases;
  const addCase = (id: string, title: string, failureKind: string) =>
    cases.push({ ...cases[0], id, title, failureKind, detailsJson: JSON.stringify({ DispatchId: "d1" }) });
  beforeEach(() => revalidateMock.mockReset());
  afterEach(() => { cases.splice(1); });

  const buttonsIn = (dialog: HTMLElement) => within(dialog).getAllByRole("button").map((button) => button.getAttribute("aria-label") ?? button.textContent);

  it("offers the buttons that fit the kind of failure", async () => {
    addCase("vanished-1", "Sintel", "vanished-downloading");
    addCase("fault-1", "Tangerine", "processedFileIsLess");
    renderActivity("/activity?status=needs-decision");

    await userEvent.click(screen.getByText("Sintel"));
    let buttons = buttonsIn(screen.getByRole("dialog"));
    expect(buttons).toEqual(expect.arrayContaining(["Search now: Sintel", "Stop wanting: Sintel", "Dismiss: Sintel"]));
    expect(buttons).not.toContain("Import original: Sintel");
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    await userEvent.click(screen.getByText("Tangerine"));
    buttons = buttonsIn(screen.getByRole("dialog"));
    expect(buttons).toEqual(expect.arrayContaining(["Retry: Tangerine", "Import original: Tangerine", "Search again: Tangerine", "Dismiss: Tangerine"]));
    expect(buttons).not.toContain("Stop wanting: Tangerine");
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    // A file that needs a person's choice (no video in it) can only be dismissed.
    await userEvent.click(screen.getByText("Arrival"));
    const plain = buttonsIn(screen.getByRole("dialog"));
    expect(plain).toContain("Dismiss: Arrival");
    expect(plain).not.toContain("Retry: Arrival");
  });

  it("retries through the shared route, says what happened and asks the sources again", async () => {
    addCase("retry-1", "Sintel", "importFailed");
    authedFetchMock.mockImplementation(async (path: string) =>
      path === "/api/movies/import-recovery/retry-1/retry" ? jsonResponse({ message: "Sintel is being tried again." }) : answerActivityApi(path));
    renderActivity("/activity?status=needs-decision");

    await userEvent.click(screen.getByText("Sintel"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Retry: Sintel" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/retry-1/retry", expect.objectContaining({ method: "POST" })));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("asks before dismissing, and does nothing if it is cancelled", async () => {
    addCase("dismiss-1", "Sintel", "importFailed");
    authedFetchMock.mockImplementation(async (path: string) =>
      path === "/api/movies/import-recovery/dismiss-1/dismiss" ? jsonResponse({}) : answerActivityApi(path));
    renderActivity("/activity?status=needs-decision");

    await userEvent.click(screen.getByText("Sintel"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss: Sintel" }));
    const confirm = screen.getByRole("dialog", { name: "Dismiss Sintel?" });
    expect(authedFetchMock).not.toHaveBeenCalledWith(expect.stringContaining("/dismiss"), expect.anything());

    await userEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(authedFetchMock).not.toHaveBeenCalledWith(expect.stringContaining("/dismiss"), expect.anything());

    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss: Sintel" }));
    await userEvent.click(within(screen.getByRole("dialog", { name: "Dismiss Sintel?" })).getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/movies/import-recovery/dismiss-1/dismiss", expect.objectContaining({ method: "POST" })));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
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

  it("lists a failed manual import even though its job names no title: an import is always about one", async () => {
    loaderData.failedJobs = [
      job({ id: "job-manual-import", jobType: "filesystem.import.execute", label: "Import a file by hand", status: "dead-letter", attempts: 3, relatedEntityType: null, relatedEntityId: null, lastError: "The file was not found." })
    ] as never;
    renderActivity("/activity?status=failed");

    expect(screen.getByRole("radio", { name: "Failed 2" })).toBeInTheDocument();
    expect(screen.getByText("Import a file by hand")).toBeInTheDocument();
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
    expect(screen.getByRole("searchbox", { name: "Find a title" })).toHaveValue("Arrival");
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

  it("dismisses a failed send from its drawer, and a send still going has no Dismiss (#1218)", async () => {
    revalidateMock.mockReset();
    renderActivity("/activity?status=failed");

    await userEvent.click(screen.getByText("Sintel.2010.720p"));
    const dismiss = within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss the failed download Sintel.2010.720p" });
    await userEvent.click(dismiss);

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-dispatches/dispatch-refused/dismiss", { method: "POST" }));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());

    // A send still on its way is not failed: nothing to dismiss.
    await userEvent.click(screen.getByRole("radio", { name: "In progress 2" }));
    await userEvent.click(screen.getByText("Arrival.2016.1080p"));
    expect(within(screen.getByRole("dialog")).queryByRole("button", { name: /Dismiss the failed download/ })).not.toBeInTheDocument();
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

/**
 * #1115: what Transfers' Recent activity listed that the history does not, now steps of the "Any step" picker: files
 * handed to the processor and back, download-health findings, and what a download client reported.
 */
describe("Activity steps built from Transfers' sources (#1115)", () => {
  const handoffs = [
    {
      id: "h1", libraryId: "l1", mediaType: "movie", clientId: "c1", queueItemId: "q1", releaseName: "Sintel.2010.1080p", sourcePath: "D:/Downloads/Sintel",
      processorName: "Weir", status: "started", outputPath: null, importJobId: null, failureMessage: null, createdUtc: "2026-10-02T08:00:00Z", updatedUtc: "2026-10-02T08:05:00Z"
    },
    {
      id: "h2", libraryId: "l1", mediaType: "movie", clientId: "c1", queueItemId: "q2", releaseName: "Arrival.2016.2160p", sourcePath: "D:/Downloads/Arrival",
      processorName: "Weir", status: "completed", outputPath: "D:/Clean/Arrival.mkv", importJobId: null, failureMessage: null, createdUtc: "2026-10-02T07:00:00Z", updatedUtc: "2026-10-02T07:10:00Z"
    }
  ];
  const health = [
    { clientId: "c1", queueItemId: "q3", releaseName: "Stalled.Show.S01E01", kind: "stalled", severity: "critical", evidence: "No progress for 6 hours.", firstObservedUtc: "2026-10-02T01:00:00Z", lastObservedUtc: "2026-10-02T07:00:00Z", strikeCount: 3, ignoredUntilUtc: null },
    { clientId: "c1", queueItemId: "q4", releaseName: "Slow.Film.2020", kind: "slow", severity: "warning", evidence: "Far below the usual speed.", firstObservedUtc: "2026-10-02T06:00:00Z", lastObservedUtc: "2026-10-02T06:30:00Z", strikeCount: 1, ignoredUntilUtc: null }
  ];
  const telemetry = {
    clients: [{
      clientId: "c1", clientName: "qBittorrent",
      history: [{ id: "x1", clientId: "c1", clientName: "qBittorrent", title: "Nope.Film.2019", releaseName: "Nope.Film.2019", outcome: "failed", indexerName: "NZBgeek", completedUtc: "2026-10-02T05:00:00Z", errorMessage: "Not enough disk space." }]
    }]
  };

  function answerLive(path: string) {
    if (path.startsWith("/api/integrations/processors/handoffs")) return jsonResponse(handoffs);
    if (path.startsWith("/api/download-health")) return jsonResponse({ items: health, nextPageToken: null, hasMore: false });
    if (path.startsWith("/api/download-clients/telemetry")) return jsonResponse(telemetry);
    return answerActivityApi(path);
  }

  beforeEach(() => {
    authedFetchMock.mockImplementation(async (path: string) => answerLive(path));
  });

  it("lists processor hand-offs under Processing, in progress and done, without asking the history", async () => {
    renderActivity("/activity?type=processing");

    expect(await screen.findByText("Sintel.2010.1080p")).toBeInTheDocument();
    expect(screen.getByText("Arrival.2016.2160p")).toBeInTheDocument();
    expect(screen.getByText("With the processor")).toBeInTheDocument();
    expect(screen.getByText("Processed and handed back")).toBeInTheDocument();
    expect(requestedActivityPaths()).toHaveLength(0);

    await userEvent.click(screen.getByRole("radio", { name: /^In progress/ }));
    expect(screen.getByText("Sintel.2010.1080p")).toBeInTheDocument();
    expect(screen.queryByText("Arrival.2016.2160p")).not.toBeInTheDocument();
  });

  it("lists download-health findings under Download health as Failed or Needs a decision", async () => {
    renderActivity("/activity?type=download-health");

    expect(await screen.findByText("No progress for 6 hours.")).toBeInTheDocument();
    expect(screen.getByText("Far below the usual speed.")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("radio", { name: /^Failed/ }));
    expect(screen.getByText("Stalled.Show.S01E01")).toBeInTheDocument();
    expect(screen.queryByText("Slow.Film.2020")).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("radio", { name: /^Needs a decision/ }));
    expect(screen.getByText("Slow.Film.2020")).toBeInTheDocument();
  });

  it("lists what a download client reported under Download client", async () => {
    renderActivity("/activity?type=download-client");

    expect(await screen.findByText("Nope.Film.2019")).toBeInTheDocument();
    expect(screen.getByText("Not enough disk space.")).toBeInTheDocument();
  });

  it("dismisses a failed hand-off, a stalled download and a failed client download from their drawers (#1218)", async () => {
    const closeDrawer = async () => {
      await userEvent.keyboard("{Escape}");
      await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    };

    // A stalled download: the same ignore the Queue's Dismiss sends.
    renderActivity("/activity?type=download-health");
    await userEvent.click(await screen.findByText("Stalled.Show.S01E01"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss the failed finding Stalled.Show.S01E01" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith(
      "/api/download-clients/c1/queue/q3/health/stalled/ignore",
      expect.objectContaining({ method: "POST", body: JSON.stringify({ durationDays: 7 }) })
    ));
    await closeDrawer();
  });

  it("dismisses a failed row of a client's history from its drawer", async () => {
    renderActivity("/activity?type=download-client");
    await userEvent.click(await screen.findByText("Nope.Film.2019"));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss the failed client download Nope.Film.2019" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-clients/c1/history/x1/dismiss", { method: "POST" }));
  });

  it("offers the three steps in the Any step picker", async () => {
    renderActivity();
    await openHistory();

    await userEvent.click(screen.getByRole("button", { name: "More filters" }));
    for (const name of ["Processing", "Download health", "Download client"]) {
      expect(await screen.findByRole("radio", { name })).toBeInTheDocument();
    }
  });
});
