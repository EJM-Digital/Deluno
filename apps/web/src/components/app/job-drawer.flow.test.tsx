import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { JobQueueItem } from "../../lib/api";
import { DEFAULT_DISPLAY_PREFERENCES, formatDateTime } from "../../lib/display-preferences";
import { JobDrawer } from "./job-drawer";

/**
 * One job opened, in every state a job can be in: what it says about the run,
 * why it failed, what it was for, what it was asked, and what can be done.
 */

const done = {
  id: "job-1",
  jobType: "library.scan",
  label: "Scan a library",
  sourceLabel: "Libraries",
  status: "completed",
  attempts: 1,
  scheduledUtc: "2026-10-08T06:52:44Z",
  startedUtc: "2026-10-08T06:53:00Z",
  completedUtc: "2026-10-08T06:54:00Z",
  workerId: "worker-7",
  lastError: null,
  payloadJson: null,
  relatedEntityType: null,
  relatedEntityId: null,
  target: null
} as unknown as JobQueueItem;

function open(job: JobQueueItem | null, extra: { busy?: boolean; onRetry?: (job: JobQueueItem) => void } = {}) {
  const onClose = vi.fn();
  const onDismiss = vi.fn();
  render(
    <MemoryRouter>
      <JobDrawer job={job} busy={extra.busy ?? false} onClose={onClose} onDismiss={onDismiss} onRetry={extra.onRetry} />
    </MemoryRouter>
  );
  return { onClose, onDismiss };
}

/** The value printed beside a fact's label. */
function fact(label: string) {
  const term = screen.getByText(label, { selector: "dt" });
  return term.nextElementSibling as HTMLElement;
}

const when = (value: string) => formatDateTime(value, DEFAULT_DISPLAY_PREFERENCES);

describe("a job drawer with no job", () => {
  it("draws nothing", () => {
    open(null);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});

describe("a finished job", () => {
  it("is titled by its label and described by where it came from and how it ended", () => {
    open(done);
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Scan a library" })).toBeInTheDocument();
    expect(screen.getByText("Libraries · Completed")).toBeInTheDocument();
  });

  it("is titled by its target, with the label before the source, when it has one", () => {
    open({ ...done, target: "Arrival (2016)" } as JobQueueItem);
    expect(screen.getByRole("heading", { name: "Arrival (2016)" })).toBeInTheDocument();
    expect(screen.getByText("Scan a library · Libraries · Completed")).toBeInTheDocument();
  });

  it("lists the run: attempts, the four times and the worker", () => {
    open(done);
    expect(screen.getByText("Run")).toBeInTheDocument();
    expect(fact("Attempts")).toHaveTextContent("1");
    expect(fact("Scheduled")).toHaveTextContent(when("2026-10-08T06:52:44Z"));
    expect(fact("Started")).toHaveTextContent(when("2026-10-08T06:53:00Z"));
    expect(fact("Finished")).toHaveTextContent(when("2026-10-08T06:54:00Z"));
    expect(fact("Worker")).toHaveTextContent("worker-7");
    expect(fact("Worker")).toHaveClass("font-mono");
    expect(fact("Attempts")).not.toHaveClass("font-mono");
  });

  it("says when it has not started, not finished or had no worker", () => {
    open({ ...done, status: "queued", startedUtc: null, completedUtc: null, workerId: null } as unknown as JobQueueItem);
    expect(fact("Started")).toHaveTextContent("Not started");
    expect(fact("Finished")).toHaveTextContent("Not finished");
    expect(fact("Worker")).toHaveTextContent("—");
    expect(screen.getByText("Libraries · Queued")).toBeInTheDocument();
  });

  it("offers neither failure section, and only Close", () => {
    open(done);
    expect(screen.queryByText("Why it failed")).not.toBeInTheDocument();
    expect(screen.queryByText("Put it right")).not.toBeInTheDocument();
    expect(screen.queryByText("If it does not need doing")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Dismiss" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
  });

  it("closes from the Close button and from Escape", async () => {
    const user = userEvent.setup();
    const { onClose } = open(done);
    await user.click(screen.getAllByRole("button", { name: "Close" })[0]);
    expect(onClose).toHaveBeenCalledTimes(1);
    await user.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});

describe("what a job was for and what it was asked", () => {
  it("names the kind and the id of what it worked on", () => {
    open({ ...done, relatedEntityType: "movie", relatedEntityId: "movie-42" } as JobQueueItem);
    expect(screen.getByText("What it was for")).toBeInTheDocument();
    expect(fact("Type")).toHaveTextContent("movie");
    expect(fact("Id")).toHaveTextContent("movie-42");
    expect(fact("Id")).toHaveClass("font-mono");
  });

  it("does not print the id of a release chosen on Search, which no one can look up", () => {
    open({ ...done, relatedEntityType: "release", relatedEntityId: "rel-9" } as JobQueueItem);
    expect(fact("Type")).toHaveTextContent("Chosen on Search");
    expect(screen.queryByText("Id", { selector: "dt" })).not.toBeInTheDocument();
    expect(fact("Type").closest("dl")!.querySelectorAll("dt")).toHaveLength(1);
  });

  it("leaves the section out when it was for nothing in particular", () => {
    open({ ...done, relatedEntityType: "movie", relatedEntityId: null } as unknown as JobQueueItem);
    expect(screen.queryByText("What it was for")).not.toBeInTheDocument();
  });

  it("lists what was asked in plain labels, ids in a fixed-width font", () => {
    open({ ...done, payloadJson: JSON.stringify({ releaseName: "Arrival.2016.1080p", dryRun: true, libraryId: "lib-1" }) } as JobQueueItem);
    expect(screen.getByText("What was asked")).toBeInTheDocument();
    expect(fact("Release")).toHaveTextContent("Arrival.2016.1080p");
    expect(fact("Release")).not.toHaveClass("font-mono");
    expect(fact("Practice run")).toHaveTextContent("Yes");
    expect(fact("Library")).toHaveClass("font-mono");
  });

  it("leaves the section out when nothing was recorded", () => {
    open({ ...done, payloadJson: "{}" } as JobQueueItem);
    expect(screen.queryByText("What was asked")).not.toBeInTheDocument();
  });
});

describe("a job that failed", () => {
  const failed = { ...done, status: "failed", lastError: "The destination\nfile already exists." } as JobQueueItem;

  it("says why, keeping the lines of the message", () => {
    open(failed);
    expect(screen.getByText("Why it failed")).toBeInTheDocument();
    const reason = screen.getByText(/The destination/);
    expect(reason).toHaveClass("whitespace-pre-wrap", "text-destructive");
    expect(reason.textContent).toBe("The destination\nfile already exists.");
  });

  it("does not claim a reason when none was recorded", () => {
    open({ ...failed, lastError: null } as JobQueueItem);
    expect(screen.queryByText("Why it failed")).not.toBeInTheDocument();
  });

  it("offers the fix link and Dismiss, and no Try again for something that is not an import", () => {
    open(failed, { onRetry: vi.fn() });
    expect(screen.getByText("Put it right")).toBeInTheDocument();
    expect(screen.getByText("If it does not need doing")).toBeInTheDocument();
    expect(screen.getByText("Dismiss this job")).toBeInTheDocument();
    expect(screen.getByText("Deluno stops counting it as failed. What it was and why it failed stays in Events.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Fix under In progress" })).toHaveAttribute("href", "/queue");
    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
  });

  it("closes the drawer when the fix link is followed", async () => {
    const user = userEvent.setup();
    const { onClose } = open(failed);
    await user.click(screen.getByRole("link", { name: "Fix under In progress" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("dismisses the job it shows", async () => {
    const user = userEvent.setup();
    const { onDismiss } = open(failed);
    await user.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(onDismiss).toHaveBeenCalledWith(failed);
  });

  it("holds Dismiss and Try again while another action is under way", () => {
    open({ ...failed, jobType: "filesystem.import.execute" } as JobQueueItem, { busy: true, onRetry: vi.fn() });
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Try again" })).toBeDisabled();
  });

  it("leaves Dismiss and Try again usable when nothing else is happening", () => {
    open({ ...failed, jobType: "filesystem.import.execute" } as JobQueueItem, { onRetry: vi.fn() });
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Try again" })).toBeEnabled();
  });

  it("offers Try again for an import only when the screen can run it again", () => {
    open({ ...failed, jobType: "filesystem.import.execute" } as JobQueueItem);
    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
  });

  it("treats a job that gave up the same as one that failed", () => {
    open({ ...failed, status: "dead-letter" } as JobQueueItem);
    expect(screen.getByText("Libraries · Gave up")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeEnabled();
  });

  it("says nothing of failure while the job is still running", () => {
    open({ ...done, status: "running" } as JobQueueItem);
    expect(screen.queryByText("Put it right")).not.toBeInTheDocument();
    expect(screen.queryByText("If it does not need doing")).not.toBeInTheDocument();
  });
});
