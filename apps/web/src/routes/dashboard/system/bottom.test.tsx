import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import type { SystemDatabasesResponse, SystemLogResponse } from "../../../lib/api/types/system-log";
import { BackupsCard, LogCard, TasksCard, maintenanceLine, sortedTasks } from "./bottom";

const NOW = Date.parse("2026-10-01T12:00:00Z");

const log: SystemLogResponse = {
  errorsToday: 1,
  warningsToday: 2,
  latestLogFile: "deluno-20261001.jsonl",
  entries: [
    { id: "a", timeUtc: "2026-10-01T11:59:00Z", level: "error", message: "The backup folder is not writable.", source: "Backup" },
    { id: "b", timeUtc: "2026-10-01T11:58:00Z", level: "warning", message: "The indexer answered slowly.", source: "HealthCheck" },
    { id: "c", timeUtc: "2026-10-01T11:57:00Z", level: "info", message: "Backup complete.", source: "Backup" }
  ]
};

describe("Scheduled tasks", () => {
  it("says why a failed task failed, in its own words, under its name", () => {
    const tasks = [
      { key: "a", name: "Reclaim seeding files", lastResult: "failed", lastCompletedUtc: "2026-10-01T11:00:00Z", nextRunUtc: "2026-10-01T12:15:00Z", lastError: "Deluge did not answer." },
      { key: "b", name: "Back up Deluno", lastResult: "completed", lastCompletedUtc: "2026-10-01T03:00:00Z", nextRunUtc: "2026-10-02T03:00:00Z", lastError: "stale" }
    ];
    expect(sortedTasks(tasks).map((row) => row.why)).toEqual(["Deluge did not answer.", null]);
    render(<MemoryRouter><TasksCard tasks={tasks} /></MemoryRouter>);
    expect(screen.getByText("Deluge did not answer.")).toBeInTheDocument();
    expect(screen.queryByText("stale")).toBeNull();
  });

  it("still gives a plain reason for a failed task the server could not explain", () => {
    expect(sortedTasks([{ key: "a", name: "A task", lastResult: "failed", lastCompletedUtc: "2026-10-01T11:00:00Z", nextRunUtc: null }])[0].why).toBe("It did not finish. The Log has the details.");
  });
});

describe("Log", () => {
  it("shows today's real entries, counts them, and filters by level", async () => {
    render(<MemoryRouter><LogCard log={log} /></MemoryRouter>);
    expect(screen.getByText("1 errors · 2 warnings today")).toBeInTheDocument();
    expect(screen.getByText("The backup folder is not writable.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("radio", { name: "Errors" }));
    expect(screen.queryByText("The indexer answered slowly.")).toBeNull();
    expect(screen.getByText("The backup folder is not writable.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("radio", { name: "Warnings" }));
    expect(screen.getByText("The indexer answered slowly.")).toBeInTheDocument();
    expect(screen.queryByText("Backup complete.")).toBeNull();
  });

  it("opens the log files page, not Activity", () => {
    render(<MemoryRouter><LogCard log={log} /></MemoryRouter>);
    expect(screen.getByRole("link", { name: "Open the full log files" })).toHaveAttribute("href", "/system");
  });

  it("says plainly when nothing has been read or logged", () => {
    const { rerender } = render(<MemoryRouter><LogCard log={null} /></MemoryRouter>);
    expect(screen.getByText("The log has not been read yet.")).toBeInTheDocument();
    rerender(<MemoryRouter><LogCard log={{ ...log, entries: [] }} /></MemoryRouter>);
    expect(screen.getByText("Nothing notable has been logged today.")).toBeInTheDocument();
  });
});

describe("Databases", () => {
  const databases: SystemDatabasesResponse = {
    databases: [{ key: "jobs", name: "Activity and jobs", sizeBytes: 96 * 1048576 }, { key: "movies", name: "Movies", sizeBytes: 41 * 1048576 }],
    totalBytes: 137 * 1048576,
    integrityCheckedUtc: "2026-10-01T08:00:00Z",
    integrityOk: true,
    integrityNote: null,
    compactedUtc: "2026-09-28T12:00:00Z"
  };

  it("words the last check and compaction", () => {
    expect(maintenanceLine(databases, NOW)).toBe("integrity checked 4 h ago · compacted 3 days ago");
    expect(maintenanceLine({ ...databases, integrityCheckedUtc: null, compactedUtc: null }, NOW)).toBe("integrity not checked yet");
    expect(maintenanceLine({ ...databases, integrityOk: false }, NOW)).toContain("integrity problem found");
  });

  it("draws each database as a name, a bar and a size", () => {
    render(<MemoryRouter><BackupsCard backups={[]} settings={undefined} databases={databases} /></MemoryRouter>);
    const card = screen.getByRole("region", { name: "Backups and databases" });
    expect(within(card).getByText(/Databases · 137 MB/)).toBeInTheDocument();
    expect(within(card).getByText("Activity and jobs")).toBeInTheDocument();
    expect(within(card).getByText("96.0 MB")).toBeInTheDocument();
    expect(within(card).getByText("Movies")).toBeInTheDocument();
  });
});
