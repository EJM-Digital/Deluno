import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { BackupsCard, LogCard, TasksCard, sortedTasks } from "./bottom";

afterEach(cleanup);

const task = (key: string, lastResult: string, nextRunUtc: string | null, extra: object = {}) => ({
  key, name: key.toUpperCase(), lastResult, lastCompletedUtc: lastResult === "not-run" ? null : "2026-10-28T10:00:00Z", nextRunUtc, lastError: null, ...extra
});

describe("System: Scheduled tasks", () => {
  it("lists running tasks first, then the one due soonest, and marks each last result as ok, failed, running or never run", () => {
    const views = sortedTasks([
      task("late", "ok", "2026-10-29T12:00:00Z"),
      task("soon", "ok", "2026-10-29T06:00:00Z"),
      task("broken", "failed", "2026-10-29T09:00:00Z", { lastError: "The disk was full" }),
      task("busy", "running", "2026-10-29T20:00:00Z"),
      task("never", "not-run", null)
    ] as never);
    expect(views.map((view) => view.key)).toEqual(["busy", "soon", "broken", "late", "never"]);
    expect(views.map((view) => view.state)).toEqual(["run", "ok", "fail", "ok", "never"]);
    expect(views.find((view) => view.key === "broken")!.why).toBe("The disk was full");
    expect(views.find((view) => view.key === "soon")!.why).toBeNull();
  });

  it("says \"No scheduled tasks have been reported yet.\" and sends \"Automation\" to the search cycles", () => {
    render(<MemoryRouter><TasksCard tasks={[]} /></MemoryRouter>);
    expect(screen.getByText("No scheduled tasks have been reported yet.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open automation" })).toHaveAttribute("href", "/search-cycles");
  });
});

describe("System: Log", () => {
  it("says \"The log has not been read yet.\" before a read, and \"Nothing notable has been logged today.\" when a read is empty", () => {
    const { unmount } = render(<MemoryRouter><LogCard log={null} /></MemoryRouter>);
    expect(screen.getByText("The log has not been read yet.")).toBeInTheDocument();
    unmount();
    render(<MemoryRouter><LogCard log={{ errorsToday: 0, warningsToday: 0, entries: [] } as never} /></MemoryRouter>);
    expect(screen.getByText("Nothing notable has been logged today.")).toBeInTheDocument();
  });

  it("says \"Nothing at this level.\" when the chosen level has no entries", () => {
    const log = { errorsToday: 0, warningsToday: 1, entries: [{ id: "w1", timeUtc: "2026-10-28T10:00:00Z", level: "warning", message: "Slow answer", source: null }] } as never;
    render(<MemoryRouter><LogCard log={log} /></MemoryRouter>);
    expect(screen.getByText("Slow answer")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("radio", { name: "Errors" }));
    expect(screen.getByText("Nothing at this level.")).toBeInTheDocument();
  });
});

describe("System: Backups and databases", () => {
  it("sends both \"Backups\" and \"Restore…\" to System's backups page", () => {
    render(<MemoryRouter><BackupsCard backups={[]} settings={undefined} databases={null} /></MemoryRouter>);
    expect(screen.getByRole("link", { name: "Open backups" })).toHaveAttribute("href", "/system/backups");
    expect(screen.getByRole("link", { name: "Restore…" })).toHaveAttribute("href", "/system/backups");
  });
});
