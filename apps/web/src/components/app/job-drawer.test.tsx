import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { JobQueueItem } from "../../lib/api";
import { JobDrawer } from "./job-drawer";

const gaveUp = {
  id: "job-1",
  jobType: "filesystem.import.execute",
  label: "Import a download",
  sourceLabel: "Imports",
  status: "dead-letter",
  attempts: 3,
  scheduledUtc: "2026-10-08T06:52:44Z",
  lastError: "The destination file already exists.",
  payloadJson: null,
  relatedEntityId: null
} as unknown as JobQueueItem;

function show(job: JobQueueItem, onRetry?: (job: JobQueueItem) => void) {
  return render(
    <MemoryRouter>
      <JobDrawer job={job} busy={false} onClose={vi.fn()} onDismiss={vi.fn()} onRetry={onRetry} />
    </MemoryRouter>
  );
}

// #1151: the "Import a download / Gave up" row showed only Dismiss and Close.
describe("JobDrawer for a job that gave up", () => {
  it("offers Try again for an import, and a fix link to Transfers, beside Dismiss", () => {
    const onRetry = vi.fn();
    show(gaveUp, onRetry);

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(onRetry).toHaveBeenCalledWith(gaveUp);
    expect(screen.getByRole("link", { name: "Fix it in Transfers" })).toHaveAttribute("href", "/queue");
    expect(screen.getByRole("button", { name: "Dismiss" })).toBeInTheDocument();
  });

  it("still links to the fix for a job that is not an import, with no Try again", () => {
    show({ ...gaveUp, jobType: "library.scan" } as JobQueueItem, vi.fn());
    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Fix it in Transfers" })).toBeInTheDocument();
  });
});
