import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventItem } from "../../lib/api";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { computeAttention } from "../../routes/dashboard/attention";
import { fullSummary } from "../../routes/dashboard/downloads-fixtures";

const fetchPageItems = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchPageItems
}));

const navigate = vi.hoisted(() => vi.fn());
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => navigate
}));

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(() => Promise.resolve(new Response("{}"))) }));

const { NotificationsBell } = await import("./notifications-bell");

const attention: AttentionSnapshot = {
  failedJobs: 2,
  deadLetteredJobs: 0,
  indexerAlerts: 0,
  downloadsNeedingAction: 0,
  failedImportJobs: 0,
  movieWanted: 0,
  tvWanted: 0,
  healthProblems: 0,
  total: 0,
  needs: computeAttention(undefined),
  loading: false
};

/** A busy install: real problems on both tabs, plus a leftover folder and a suggestion that are not counted. */
function busy(): AttentionSnapshot {
  const needs = computeAttention(
    fullSummary,
    {
      failedJobs: [{ id: "j1", jobType: "filesystem.import.execute", status: "failed", lastError: "Disk was full", attempts: 3, maxAttempts: 3, nextAttemptUtc: null }] as never,
      handoffs: [{ id: "h1", releaseName: "Metropolis (1927)", status: "failed", failureMessage: "The processor timed out.", processorName: "Cleaner", updatedUtc: "2026-09-30T09:55:00Z" }] as never,
      orphans: [{ name: "a" }, { name: "b" }] as never
    }
  );
  return { ...attention, total: needs.total, needs };
}

const events: ActivityEventItem[] = [
  {
    id: "evt-3",
    category: "job.failed",
    message: "Import failed for The Newest Film",
    severity: "error",
    detailsJson: null,
    relatedJobId: "job-3",
    relatedEntityType: "movie",
    relatedEntityId: "movie-3",
    createdUtc: "2026-09-27T12:03:00Z"
  },
  {
    id: "evt-2",
    category: "download_dispatch.sent",
    message: "Sent The Middle Episode to qBittorrent",
    severity: "info",
    detailsJson: null,
    relatedJobId: null,
    relatedEntityType: "download_dispatch",
    relatedEntityId: "dispatch-2",
    createdUtc: "2026-09-27T12:02:00Z"
  },
  {
    id: "evt-1",
    category: "filesystem.import.completed",
    message: "Imported The Oldest Film",
    severity: "info",
    detailsJson: null,
    relatedJobId: null,
    relatedEntityType: null,
    relatedEntityId: null,
    createdUtc: "2026-09-27T12:01:00Z"
  }
];

async function openPanel() {
  const user = userEvent.setup();
  render(<MemoryRouter><NotificationsBell attention={attention} /></MemoryRouter>);
  await user.click(screen.getByRole("button", { name: /notifications/i }));
  await waitFor(() => expect(fetchPageItems).toHaveBeenCalled());
  return user;
}

describe("notifications bell (#907)", () => {
  afterEach(() => {
    fetchPageItems.mockReset();
    navigate.mockReset();
  });

  it("shows no badge at zero, and the real-problem count when there is one", () => {
    fetchPageItems.mockResolvedValue([]);
    const { unmount } = render(<MemoryRouter><NotificationsBell attention={attention} /></MemoryRouter>);
    const quiet = screen.getByRole("button", { name: "Notifications" });
    expect(quiet.textContent).toBe("");
    unmount();

    const snapshot = busy();
    expect(snapshot.total).toBeGreaterThan(0);
    render(<MemoryRouter><NotificationsBell attention={snapshot} /></MemoryRouter>);
    const bell = screen.getByRole("button", { name: `Notifications — ${snapshot.total} things need a look` });
    expect(bell.textContent).toBe(String(snapshot.total));
    // The number is the two Dashboard badges added together; leftovers and suggestions are not in it.
    expect(snapshot.total).toBe(snapshot.needs.counts.live + snapshot.needs.counts.system);
    expect(snapshot.needs.alsoWorthALook.leftovers).toBe(2);
  });

  it("lists Needs you first, with the Dashboard's rows and buttons, then the recent events", async () => {
    fetchPageItems.mockResolvedValue(events);
    const user = userEvent.setup();
    render(<MemoryRouter><NotificationsBell attention={busy()} /></MemoryRouter>);
    await user.click(screen.getByRole("button", { name: /notifications/i }));
    await waitFor(() => expect(fetchPageItems).toHaveBeenCalled());

    const dialog = await screen.findByRole("dialog");
    const needs = within(dialog).getByRole("region", { name: "Needs you" });
    expect(within(needs).getByRole("button", { name: "Retry failed import 1" })).toBeInTheDocument();
    expect(within(needs).getByRole("button", { name: "Retry Metropolis (1927)" })).toBeInTheDocument();
    expect(within(needs).getByRole("button", { name: "Give up on Metropolis (1927)" })).toBeInTheDocument();
    // A setup problem is reachable from here too, by a button that goes to where it is cleared.
    expect(within(needs).getAllByRole("link", { name: /System/ })[0]).toHaveAttribute("href", "/?tab=system");
    // The quiet line is there and is a link, not a count.
    expect(within(needs).getByRole("link", { name: "2 leftover folders" })).toHaveAttribute("href", "/queue");

    // Needs you comes before the recent events.
    const recent = within(dialog).getByText("Recent");
    expect(needs.compareDocumentPosition(recent) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(await within(dialog).findByRole("button", { name: /Import failed for The Newest Film/ })).toBeInTheDocument();
  });

  it("says nothing needs you when nothing does", async () => {
    fetchPageItems.mockResolvedValue([]);
    await openPanel();
    expect(within(screen.getByRole("region", { name: "Needs you" })).getByText("Nothing needs you right now.")).toBeInTheDocument();
  });

  it("opens on click and lists the most recent events newest first", async () => {
    fetchPageItems.mockResolvedValue(events);
    await openPanel();

    expect(fetchPageItems).toHaveBeenCalledWith("/api/activity?pageSize=60");
    const rows = await screen.findAllByRole("button", { name: /Import failed|Sent The Middle|Imported The Oldest/ });
    expect(rows.map((row) => row.textContent)).toEqual([
      expect.stringContaining("Import failed for The Newest Film"),
      expect.stringContaining("Sent The Middle Episode to qBittorrent"),
      expect.stringContaining("Imported The Oldest Film")
    ]);
  });

  it("marks a failure clearly and leaves ordinary events unmarked", async () => {
    fetchPageItems.mockResolvedValue(events);
    await openPanel();

    const failedRow = await screen.findByRole("button", { name: /Import failed for The Newest Film/ });
    expect(within(failedRow).getByText("Failed")).toBeTruthy();

    const quietRow = screen.getByRole("button", { name: /Imported The Oldest Film/ });
    expect(within(quietRow).queryByText("Failed")).toBeNull();
    expect(within(quietRow).queryByText("Attention")).toBeNull();
  });

  it("sends a movie event to the title, a dispatch event to the Queue, and an untargeted one to Activity", async () => {
    fetchPageItems.mockResolvedValue(events);
    const user = await openPanel();

    await user.click(await screen.findByRole("button", { name: /Import failed for The Newest Film/ }));
    expect(navigate).toHaveBeenLastCalledWith("/movies/movie-3");

    await user.click(screen.getByRole("button", { name: /Notifications/i }));
    await user.click(await screen.findByRole("button", { name: /Sent The Middle Episode/ }));
    expect(navigate).toHaveBeenLastCalledWith("/queue");

    await user.click(screen.getByRole("button", { name: /Notifications/i }));
    await user.click(await screen.findByRole("button", { name: /Imported The Oldest Film/ }));
    expect(navigate).toHaveBeenLastCalledWith("/activity");
  });

  it("ends the list with Show all activity, which goes to Activity and closes the panel", async () => {
    fetchPageItems.mockResolvedValue(events);
    const user = await openPanel();

    await user.click(await screen.findByRole("button", { name: "Show all activity" }));
    expect(navigate).toHaveBeenLastCalledWith("/activity");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("closes on Escape and returns focus to the bell", async () => {
    fetchPageItems.mockResolvedValue(events);
    const user = await openPanel();

    expect(screen.getByRole("dialog")).toBeTruthy();
    await user.keyboard("{Escape}");

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.getByRole("button", { name: /notifications/i })).toHaveFocus();
  });

  it("closes on a click outside the panel", async () => {
    fetchPageItems.mockResolvedValue(events);
    const user = await openPanel();

    expect(screen.getByRole("dialog")).toBeTruthy();
    await user.click(document.body);

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("is keyboard reachable: every row and Show all activity are real buttons in the panel", async () => {
    fetchPageItems.mockResolvedValue(events);
    await openPanel();

    const dialog = screen.getByRole("dialog");
    const buttons = within(dialog).getAllByRole("button");
    expect(buttons.map((b) => b.textContent).join(" ")).toContain("Show all activity");
    // Nothing in the panel is left unreachable behind a negative tabindex.
    for (const button of buttons) {
      expect(button.getAttribute("tabindex")).not.toBe("-1");
    }
  });
});

describe("the bell shows what matters, not routine job noise (#916)", () => {
  afterEach(() => {
    fetchPageItems.mockReset();
    navigate.mockReset();
  });

  const routineRun: ActivityEventItem[] = [
    {
      id: "job-completed",
      category: "job.completed",
      message: "Every file Deluno holds has been read and evaluated under its current preference plan. (Movies)",
      severity: "info",
      detailsJson: null,
      relatedJobId: "job-42",
      relatedEntityType: "movie",
      relatedEntityId: null,
      createdUtc: "2026-09-27T12:05:00Z"
    },
    {
      id: "job-started",
      category: "job.started",
      message: "Started reading what your files actually are (Movies).",
      severity: "info",
      detailsJson: null,
      relatedJobId: "job-42",
      relatedEntityType: "movie",
      relatedEntityId: null,
      createdUtc: "2026-09-27T12:04:00Z"
    },
    {
      id: "job-queued",
      category: "job.queued",
      message: "Added a media read (Movies) to the queue.",
      severity: "info",
      detailsJson: null,
      relatedJobId: "job-42",
      relatedEntityType: "movie",
      relatedEntityId: null,
      createdUtc: "2026-09-27T12:03:00Z"
    }
  ];

  it("does not show a routine background job's own queued/started/completed lines", async () => {
    fetchPageItems.mockResolvedValue(routineRun);
    await openPanel();

    expect(screen.getByText("Nothing has happened yet.")).toBeInTheDocument();
  });

  it("shows what needs attention beside routine noise, not crowded out by it", async () => {
    fetchPageItems.mockResolvedValue([...routineRun, ...events]);
    await openPanel();

    const rows = screen.getAllByRole("button").filter((button) => button.textContent?.includes("Import failed") || button.textContent?.includes("Sent The Middle") || button.textContent?.includes("Imported The Oldest"));
    expect(rows).toHaveLength(3);
    expect(screen.queryByText(/Started reading what your files/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Added a media read/)).not.toBeInTheDocument();
  });
});
