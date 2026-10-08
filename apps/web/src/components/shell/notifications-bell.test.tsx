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

const authedFetch = vi.hoisted(() => vi.fn((_path: string, _init?: RequestInit) => Promise.resolve(new Response("{}"))));
vi.mock("../../lib/use-auth", () => ({ authedFetch }));

const { NotificationsBell } = await import("./notifications-bell");
const { queryClient } = await import("../../lib/route-cache");

const attention: AttentionSnapshot = {
  failedJobs: 2,
  failedTitleJobs: 0,
  failedBackgroundJobs: 2,
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
    queryClient.clear();
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
    expect(within(needs).getByRole("button", { name: "Try again: failed import 1" })).toBeInTheDocument();
    expect(within(needs).getByRole("button", { name: "Retry: Metropolis (1927)" })).toBeInTheDocument();
    // "Give up" is Dismiss now, and says on hover that nothing is deleted.
    const dismiss = within(needs).getByRole("button", { name: "Dismiss: Metropolis (1927)" });
    expect(dismiss).toHaveAttribute("title", "Stop showing this. Nothing is deleted.");
    expect(within(needs).queryByText("Give up")).toBeNull();
    // A Deluno row links into System's Health, and its button goes to where it is cleared.
    expect(within(needs).getAllByRole("link", { name: /System/ })[0]).toHaveAttribute("href", "/system");
    // The quiet line is there and is a link, not a count.
    expect(within(needs).getByRole("link", { name: "2 leftover folders" })).toHaveAttribute("href", "/queue");

    // Needs you comes before the recent events.
    const recent = within(dialog).getByText("Recent");
    expect(needs.compareDocumentPosition(recent) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(await within(dialog).findByRole("button", { name: /Import failed for The Newest Film/ })).toBeInTheDocument();
  });


  it("draws every row with the same two action columns and unique button names", async () => {
    fetchPageItems.mockResolvedValue([]);
    const user = userEvent.setup();
    render(<MemoryRouter><NotificationsBell attention={busy()} /></MemoryRouter>);
    await user.click(screen.getByRole("button", { name: /notifications/i }));
    const needs = within(await screen.findByRole("dialog")).getByRole("region", { name: "Needs you" });

    const rows = within(needs).getAllByRole("listitem");
    expect(rows.length).toBeGreaterThan(2);
    for (const row of rows) {
      const actions = row.querySelector("[data-row-actions]") as HTMLElement;
      // Two fixed columns on every row; a single action sits in the first and the second stays empty.
      expect(actions.children).toHaveLength(2);
      expect(actions.children[0].children.length).toBe(1);
      expect(actions.children[1].children.length).toBeLessThanOrEqual(1);
      for (const control of actions.querySelectorAll("button, a")) {
        expect(control.className).toContain("w-30");
        expect(control.className).toContain("h-7");
        expect(control.className).toContain("whitespace-nowrap");
      }
    }
    const names = within(needs).getAllByRole("button").concat(within(needs).getAllByRole("link")).map((el) => el.getAttribute("aria-label") ?? el.textContent);
    expect(new Set(names).size).toBe(names.length);
  });
  it("says nothing needs you when nothing does", async () => {
    fetchPageItems.mockResolvedValue([]);
    await openPanel();
    expect(within(screen.getByRole("region", { name: "Needs you" })).getByText("Nothing needs you right now.")).toBeInTheDocument();
  });

  it("opens on click and lists the most recent events newest first", async () => {
    fetchPageItems.mockResolvedValue(events);
    await openPanel();

    expect(fetchPageItems).toHaveBeenCalledWith("/api/activity?pageSize=400");
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

  it("sends every row to its own entry in the Activity history, opened on that event's day (#1100)", async () => {
    fetchPageItems.mockResolvedValue(events);
    const user = await openPanel();

    for (const [name, id] of [[/Import failed for The Newest Film/, "evt-3"], [/Sent The Middle Episode/, "evt-2"], [/Imported The Oldest Film/, "evt-1"]] as const) {
      await user.click(await screen.findByRole("button", { name }));
      expect(String(navigate.mock.lastCall?.[0])).toMatch(new RegExp("^/activity\\?event=" + id + "&since=\\d{4}-\\d{2}-\\d{2}&until=\\d{4}-\\d{2}-\\d{2}$"));
      await user.click(screen.getByRole("button", { name: /Notifications/i }));
    }
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
    queryClient.clear();
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

    expect(screen.queryByText("Nothing has happened yet.")).not.toBeInTheDocument();
    expect(screen.queryByText("Recent")).not.toBeInTheDocument();
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

describe("the bell is two sections, with the same buttons System's Health shows (#1098)", () => {
  afterEach(() => {
    fetchPageItems.mockReset();
    queryClient.clear();
    navigate.mockReset();
    authedFetch.mockClear();
  });

  const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();

  function mixed(): AttentionSnapshot {
    const summary = structuredClone(fullSummary);
    summary.health = {
      ...summary.health!,
      problems: 1,
      topProblems: [{ id: "machine:sleep", title: "Computer sleep", category: "machine", message: "This computer goes to sleep after 30 minutes on mains power.", sinceUtc: hoursAgo(1), link: "/system", snoozed: false }]
    };
    const needs = computeAttention(summary, {
      telemetry: { clients: [{ queue: [{ id: "t1", clientId: "qb", clientName: "qBittorrent", title: "Sintel (2010)", releaseName: "Sintel.2010.1080p", status: "stalled", errorMessage: null, healthFindings: null, addedUtc: hoursAgo(2) }] }] } as never,
      noRelease: [{ mediaType: "movie", id: "m1", title: "Obscure Film", year: 1971, libraryId: "lib", lastSearchUtc: hoursAgo(1), missingSinceUtc: hoursAgo(5), reason: null }]
    });
    return { ...attention, total: needs.total, needs };
  }

  async function open() {
    fetchPageItems.mockResolvedValue([]);
    const user = userEvent.setup();
    render(<MemoryRouter><NotificationsBell attention={mixed()} /></MemoryRouter>);
    await user.click(screen.getByRole("button", { name: /notifications/i }));
    const dialog = await screen.findByRole("dialog");
    return { user, dialog };
  }

  it("lists your media and Deluno apart, each row with at most two buttons that say what they do on hover", async () => {
    const { dialog } = await open();

    const media = within(dialog).getByRole("region", { name: "Your media" });
    const deluno = within(dialog).getByRole("region", { name: "Deluno" });
    expect(within(media).getByText("Sintel (2010)")).toBeInTheDocument();
    expect(within(media).getByText("Obscure Film (1971)")).toBeInTheDocument();
    expect(within(deluno).getByText("Computer sleep")).toBeInTheDocument();
    // The stuck download is named, with its reason, and offers the stronger fix by name.
    expect(within(media).getByText("Not moving in qBittorrent.")).toBeInTheDocument();
    expect(within(media).getByRole("button", { name: "Find another: Sintel (2010)" })).toHaveAttribute("title", expect.stringContaining("blocklists this release"));
    expect(within(media).getByRole("button", { name: "Search now: Obscure Film (1971)" })).toBeInTheDocument();
    expect(within(media).getByRole("button", { name: "Dismiss: Obscure Film (1971)" })).toHaveAttribute("title", expect.stringContaining("Nothing is deleted"));

    for (const row of within(dialog).getAllByRole("listitem")) {
      const actions = row.querySelector("[data-row-actions]") as HTMLElement;
      expect(actions.querySelectorAll("button, a").length).toBeLessThanOrEqual(2);
      for (const control of actions.querySelectorAll("button, a")) expect(control.getAttribute("title")).toBeTruthy();
    }
    // A Deluno row's title links to System's Health.
    expect(within(deluno).getByRole("link", { name: "Computer sleep" })).toHaveAttribute("href", "/system");
  });

  it("Keep awake turns Deluno's keep-awake on without touching anything else", async () => {
    const { user, dialog } = await open();

    await user.click(within(dialog).getByRole("button", { name: "Keep awake: Computer sleep" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    const [path, init] = authedFetch.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(String(init.body))).toEqual({ keepComputerAwake: true });
  });

  it("Find another removes that download and searches again, and Dismiss on a title hides it without changing it", async () => {
    const { user, dialog } = await open();

    await user.click(within(dialog).getByRole("button", { name: "Find another: Sintel (2010)" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    expect(authedFetch.mock.calls[0][0]).toBe("/api/download-clients/qb/queue/t1/remove");
    expect(JSON.parse(String((authedFetch.mock.calls[0][1] as RequestInit).body))).toEqual({ reason: "failed" });

    authedFetch.mockClear();
    await user.click(within(dialog).getByRole("button", { name: "Dismiss: Obscure Film (1971)" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    expect(authedFetch.mock.calls[0][0]).toBe("/api/attention/no-release/movie/m1/dismiss");
  });
});
