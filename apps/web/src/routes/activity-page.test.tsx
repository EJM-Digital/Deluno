import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventItem, MovieImportRecoverySummary, SeriesImportRecoverySummary } from "../lib/api";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const loaderData = {
  activity: [
    {
      id: "event-1",
      category: "release.blocked",
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
  ] as ActivityEventItem[],
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
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { ActivityPage } = await import("./activity-page");

/**
 * #798: an event-type filter that actually narrows the list, and a rejected
 * import's specific reason surfaced instead of a generic label.
 */
describe("Activity events filter and columns (#798)", () => {
  it("narrows the Events list to the chosen event type", async () => {
    render(
      <MemoryRouter>
        <ActivityPage />
      </MemoryRouter>
    );

    await userEvent.click(screen.getByRole("radio", { name: "Events" }));

    expect(screen.getByText("Deluno will not use Arrival.2016.2160p again.")).toBeInTheDocument();
    expect(screen.getByText("Imported Arrival (2016).")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("combobox", { name: "Filter by event type" }));
    await userEvent.click(screen.getByRole("option", { name: "Blocklisted" }));

    expect(screen.getByText("Deluno will not use Arrival.2016.2160p again.")).toBeInTheDocument();
    expect(screen.queryByText("Imported Arrival (2016).")).not.toBeInTheDocument();
  });

  it("hides an optional column once toggled off from the column picker", async () => {
    render(
      <MemoryRouter>
        <ActivityPage />
      </MemoryRouter>
    );
    await userEvent.click(screen.getByRole("radio", { name: "Events" }));

    expect(screen.getByRole("columnheader", { name: "Category" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Columns" }));
    await userEvent.click(screen.getByRole("menuitemcheckbox", { name: "Category" }));

    expect(screen.queryByRole("columnheader", { name: "Category" })).not.toBeInTheDocument();
  });
});

describe("Import issues show the specific rejection reason (#798)", () => {
  it("names the actual failure in plain words rather than a generic label", async () => {
    render(
      <MemoryRouter>
        <ActivityPage />
      </MemoryRouter>
    );
    await userEvent.click(screen.getByRole("radio", { name: "Import issues" }));

    expect(screen.getByText("No video in the file · Arrival")).toBeInTheDocument();
    expect(screen.getByText("No video stream was detected in this file.")).toBeInTheDocument();
  });

  it("opens the full reason, recommendation and evidence in a drawer", async () => {
    render(
      <MemoryRouter>
        <ActivityPage />
      </MemoryRouter>
    );
    await userEvent.click(screen.getByRole("radio", { name: "Import issues" }));
    await userEvent.click(screen.getByText("No video in the file · Arrival"));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("No video stream was detected in this file.")).toBeInTheDocument();
    expect(within(dialog).getByText(/Choose a valid video file/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Arrival\.mkv/)).toBeInTheDocument();
  });
});

describe("A routine job's queued/started/completed fold into one row (#916)", () => {
  it("shows one row for the job run, not three", async () => {
    render(
      <MemoryRouter>
        <ActivityPage />
      </MemoryRouter>
    );
    await userEvent.click(screen.getByRole("radio", { name: "Events" }));

    expect(screen.getByText("Every file Deluno holds has been read and evaluated under its current preference plan. (Movies)")).toBeInTheDocument();
    expect(screen.queryByText("Started reading what your files actually are (Movies).")).not.toBeInTheDocument();
    expect(screen.queryByText("Added a media read (Movies) to the queue.")).not.toBeInTheDocument();
  });

  it("opens the queued and started steps in the drawer", async () => {
    render(
      <MemoryRouter>
        <ActivityPage />
      </MemoryRouter>
    );
    await userEvent.click(screen.getByRole("radio", { name: "Events" }));
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
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
  });

  it("lists what did not match, in plain words, with the file names", async () => {
    render(
      <MemoryRouter>
        <ActivityPage />
      </MemoryRouter>
    );
    await userEvent.click(screen.getByRole("radio", { name: "Events" }));
    await userEvent.click(screen.getByText("Some Show season pack imported 9 season-pack files covering 9 episodes."));

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("What didn't match")).toBeInTheDocument();
    expect(within(dialog).getByText("Some.Show.S01.Extras.Making.Of.mkv")).toBeInTheDocument();
    expect(within(dialog).getByText("The filename does not resolve to one or more canonical episodes in this show.")).toBeInTheDocument();
    expect(within(dialog).getByText("Some.Show.S01E05.mkv")).toBeInTheDocument();
    expect(within(dialog).getAllByText(/Matched to episode\(s\) S01E05 but not imported/).length).toBeGreaterThan(0);
  });

  it("confirms, names the files, deletes them, and clears the problem from the screen", async () => {
    authedFetchMock.mockResolvedValue({
      ok: true,
      json: async () => ({ deleted: 2, refused: [] })
    });

    render(
      <MemoryRouter>
        <ActivityPage />
      </MemoryRouter>
    );
    await userEvent.click(screen.getByRole("radio", { name: "Events" }));
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
