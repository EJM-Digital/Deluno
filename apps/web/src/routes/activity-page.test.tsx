import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { ActivityEventItem, MovieImportRecoverySummary, SeriesImportRecoverySummary } from "../lib/api";

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

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn(), state: "idle" })
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
