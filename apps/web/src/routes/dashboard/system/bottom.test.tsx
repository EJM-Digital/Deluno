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

  it("links to Automation and lists each task with its state, its last result and when it runs next", () => {
    const now = Date.now();
    const iso = (offsetMs: number) => new Date(now + offsetMs).toISOString();
    const tasks = [
      { key: "a", name: "Check for updates", lastResult: "completed", lastCompletedUtc: iso(-3 * 3600_000), nextRunUtc: iso(2 * 3600_000), lastError: null },
      { key: "b", name: "Reclaim seeding files", lastResult: "failed", lastCompletedUtc: iso(-10 * 60_000), nextRunUtc: iso(30 * 60_000), lastError: "Deluge did not answer." },
      { key: "c", name: "Search libraries", lastResult: "running", lastCompletedUtc: null, nextRunUtc: null, lastError: null },
      { key: "d", name: "Back up Deluno", lastResult: "not-run", lastCompletedUtc: null, nextRunUtc: iso(26 * 3600_000), lastError: null }
    ];
    render(<MemoryRouter><TasksCard tasks={tasks} /></MemoryRouter>);

    expect(screen.getByRole("link", { name: "Open automation" })).toHaveAttribute("href", "/search-cycles");
    expect(screen.getByRole("link", { name: "Open automation" }).textContent).toBe("Automation →");
    expect(screen.getByText("1 running · 1 failed")).toBeInTheDocument();
    const rowOf = (name: string) => screen.getByText(name, { selector: ".sy-nm" }).closest(".sy-tk-row") as HTMLElement;
    // Running first, then the one due soonest, then the ones with no next time.
    expect(Array.from(document.querySelectorAll(".sy-tk-row .sy-nm")).map((cell) => cell.firstChild?.textContent)).toEqual(["Search libraries", "Reclaim seeding files", "Check for updates", "Back up Deluno"]);
    expect(rowOf("Search libraries").textContent).toContain("running");
    expect(rowOf("Reclaim seeding files").textContent).toContain("✗");
    expect(rowOf("Reclaim seeding files").textContent).toContain("10 min ago");
    expect(rowOf("Reclaim seeding files").textContent).toContain("Deluge did not answer.");
    expect(rowOf("Check for updates").textContent).toContain("✓");
    expect(rowOf("Check for updates").textContent).toContain("3 h ago");
    expect(rowOf("Check for updates").textContent).toContain("2 h");
    expect(rowOf("Back up Deluno").textContent).toContain("—");
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

  it("says a repeated line once, with how many times, even with another line between, and keeps separate lines apart", () => {
    const refused = "Seerr webhook for connection 01a1 refused: the Authorization header did not match its webhook secret.";
    const repeated: SystemLogResponse = {
      ...log,
      entries: [
        { id: "r1", timeUtc: "2026-10-01T11:59:30Z", level: "warning", message: refused, source: "Seerr" },
        { id: "r2", timeUtc: "2026-10-01T11:59:20Z", level: "warning", message: refused, source: "Seerr" },
        { id: "r3", timeUtc: "2026-10-01T11:59:10Z", level: "warning", message: refused, source: "Seerr" },
        { id: "b", timeUtc: "2026-10-01T11:58:00Z", level: "warning", message: "The indexer answered slowly.", source: "HealthCheck" },
        { id: "r4", timeUtc: "2026-10-01T11:57:00Z", level: "warning", message: refused, source: "Seerr" }
      ]
    };
    render(<MemoryRouter><LogCard log={repeated} /></MemoryRouter>);
    expect(screen.getAllByText(refused)).toHaveLength(1);
    expect(screen.getByLabelText("4 times")).toHaveTextContent("×4");
    expect(screen.getByText("The indexer answered slowly.")).toBeInTheDocument();
    expect(screen.queryByLabelText("1 times")).toBeNull();
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
