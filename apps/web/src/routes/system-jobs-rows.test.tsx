import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

/** System > Background jobs card, as a person reads it (#1329): the count, the empty state, the requeue toast, the drawer and the chips. */

const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch: authedFetchMock }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", () => ({ toast: toastMock, Toaster: () => null }));

const { BackgroundJobsCard } = await import("./system-screen");

const base = { source: "schedule", sourceLabel: "The schedule", attempts: 1, maxAttempts: 3, nextAttemptUtc: null, createdUtc: "2026-10-01T00:00:00Z", scheduledUtc: "2026-10-01T00:00:00Z", startedUtc: null, completedUtc: null, workerId: null, payloadJson: null, lastError: null, relatedEntityType: null, relatedEntityId: null };
const job = (id: string, label: string, status: string, over: Record<string, unknown> = {}) => ({ ...base, id, jobType: "library.refresh", label, status, ...over });

function card(jobs: unknown[], failedJobs: unknown[] = [], onChanged = vi.fn()) {
  render(<MemoryRouter><BackgroundJobsCard jobs={jobs as never} failedJobs={failedJobs as never} onChanged={onChanged} /></MemoryRouter>);
  return onChanged;
}

afterEach(() => {
  authedFetchMock.mockReset();
  toastMock.success.mockReset();
  toastMock.error.mockReset();
});

describe("The Background jobs card count and empty state", () => {
  it("reads 'N working, M recent' when nothing is stopped, 'N stopped' when something is, and 'No background jobs yet' when nothing has run", () => {
    const first = render(<MemoryRouter><BackgroundJobsCard jobs={[]} failedJobs={[]} onChanged={vi.fn()} /></MemoryRouter>);
    expect(screen.getByText("No background jobs yet")).toBeInTheDocument();
    expect(screen.queryByText(/working|stopped/)).not.toBeInTheDocument();
    first.unmount();

    const second = render(<MemoryRouter><BackgroundJobsCard jobs={[job("a", "Refresh the whole library", "running"), job("b", "Nightly backup", "completed"), job("c", "Clean the recycle bin", "completed")] as never} failedJobs={[]} onChanged={vi.fn()} /></MemoryRouter>);
    expect(screen.getByText("1 working, 2 recent")).toBeInTheDocument();
    second.unmount();

    const dead = job("d", "Refresh all metadata", "dead-letter", { attempts: 3, lastError: "The metadata source did not answer." });
    render(<MemoryRouter><BackgroundJobsCard jobs={[job("a", "Refresh the whole library", "running"), dead] as never} failedJobs={[dead] as never} onChanged={vi.fn()} /></MemoryRouter>);
    expect(screen.getByText("1 stopped")).toBeInTheDocument();
  });
});

describe("Retry failed jobs", () => {
  const dead = job("d", "Refresh all metadata", "dead-letter", { attempts: 3, lastError: "The metadata source did not answer." });

  it("requeues them with a POST, says how many were requeued (singular and plural) and reads the card again", async () => {
    authedFetchMock.mockResolvedValue({ ok: true, json: () => Promise.resolve({ retried: 1 }) });
    const onChanged = card([dead], [dead]);

    fireEvent.click(screen.getByRole("button", { name: "Retry failed jobs" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("1 failed job requeued"));
    expect(authedFetchMock).toHaveBeenCalledWith("/api/jobs/retry-failed", { method: "POST" });
    expect(onChanged).toHaveBeenCalledTimes(1);

    authedFetchMock.mockResolvedValue({ ok: true, json: () => Promise.resolve({ retried: 3 }) });
    fireEvent.click(screen.getByRole("button", { name: "Retry failed jobs" }));
    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("3 failed jobs requeued"));
  });

  it("shows an error toast and does not read the card again when the server refuses or cannot be reached", async () => {
    authedFetchMock.mockResolvedValueOnce({ ok: false, json: () => Promise.resolve({}) });
    const onChanged = card([dead], [dead]);

    fireEvent.click(screen.getByRole("button", { name: "Retry failed jobs" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Failed jobs could not be requeued."));

    authedFetchMock.mockRejectedValueOnce(new Error("Could not reach Deluno."));
    fireEvent.click(screen.getByRole("button", { name: "Retry failed jobs" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Could not reach Deluno."));
    expect(onChanged).not.toHaveBeenCalled();
    expect(toastMock.success).not.toHaveBeenCalled();
  });
});

describe("A job opened from the card", () => {
  it("opens its drawer with the run's facts, why a dead-letter job failed in plain words and what was asked, and the chip says Gave up", async () => {
    const dead = job("d", "Refresh all metadata", "dead-letter", {
      attempts: 3, lastError: "The metadata source did not answer.", startedUtc: "2026-10-01T00:00:05Z", completedUtc: "2026-10-01T00:01:00Z", workerId: "worker-7",
      payloadJson: "{\"scope\":\"all\"}"
    });
    card([dead], [dead]);

    expect(within(document.body).getAllByText("Gave up").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByText("Refresh all metadata"));

    const drawer = await screen.findByRole("dialog");
    for (const label of ["Attempts", "Scheduled", "Started", "Finished", "Worker"]) expect(within(drawer).getByText(label)).toBeInTheDocument();
    expect(within(drawer).getByText("3")).toBeInTheDocument();
    expect(within(drawer).getByText("worker-7")).toBeInTheDocument();
    expect(within(drawer).getByText("Why it failed")).toBeInTheDocument();
    expect(within(drawer).getByText("The metadata source did not answer.")).toBeInTheDocument();
    expect(within(drawer).queryByText("What it was for")).not.toBeInTheDocument();
    expect(within(drawer).getByText("What was asked")).toBeInTheDocument();
    expect(within(drawer).getByText("Put it right")).toBeInTheDocument();
    expect(within(drawer).queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
  });

  it("shows Retrying for a failed job with another attempt coming, Failed otherwise, Completed and Running for the rest, and counts only the stopped one", () => {
    card([
      job("r", "Refresh the posters", "failed", { attempts: 1, nextAttemptUtc: "2026-10-01T00:05:00Z" }),
      job("f", "Rebuild the index", "failed", { attempts: 3, nextAttemptUtc: null, lastError: "Disk full." }),
      job("c", "Nightly backup", "completed"),
      job("u", "Refresh the whole library", "running")
    ], [job("f", "Rebuild the index", "failed", { attempts: 3, nextAttemptUtc: null, lastError: "Disk full." })]);

    expect(screen.getByText("Retrying")).toBeInTheDocument();
    expect(screen.getAllByText("Failed")).toHaveLength(1);
    expect(screen.getByText("Completed")).toBeInTheDocument();
    expect(screen.getByText("Running")).toBeInTheDocument();
    expect(screen.getByText("1 stopped")).toBeInTheDocument();
  });
});
