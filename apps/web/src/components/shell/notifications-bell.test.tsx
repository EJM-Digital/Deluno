import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventItem } from "../../lib/api";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { emptyAttentionSummary } from "../../lib/attention-summary";
import { serverRow, summaryOf } from "../../lib/attention-summary-fixtures";

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
  summary: emptyAttentionSummary,
  loading: false
};

/** A busy install: real problems in both sections, a ready update, and a leftover folder and a suggestion that are not counted. */
function busy(): AttentionSnapshot {
  const summary = summaryOf(
    [
      serverRow({ id: "import:j1", kind: "import", section: "media", tone: "bad", title: "An import didn’t finish", text: "Disk was full", to: "/queue", data: { id: "j1" } }),
      serverRow({
        id: "handoff:h1", kind: "handoff", section: "media", tone: "bad", title: "Metropolis (1927)", text: "The processor timed out.", to: "/queue",
        data: { id: "h1", releaseName: "Metropolis (1927)", status: "failed", outputPath: null, importJobId: null, outcome: null }
      }),
      serverRow({ id: "health:system:backup", kind: "health:system", title: "A drive is nearly full", data: { id: "system:backup", category: "system", name: "A drive is nearly full", link: "/system" } })
    ],
    [serverRow({ id: "update", kind: "update", title: "A new version of Deluno is ready", to: "/system/updates", action: "Open Updates" })],
    { leftovers: 2, suggestions: 0 }
  );
  return { ...attention, total: summary.count, summary };
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
    // The number is the server's: three problems. The ready update, leftovers and suggestions are not in it.
    expect(snapshot.total).toBe(3);
    expect(snapshot.summary.notices).toHaveLength(1);
    expect(snapshot.summary.leftovers).toBe(2);
  });

  it("lists Needs you first, with the server's rows and their buttons, then the recent events", async () => {
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
    const summary = summaryOf([
      serverRow({
        id: "stuck:qb:t1", kind: "stuck", section: "media", tone: "bad", title: "Sintel (2010)", text: "Not moving in qBittorrent.", to: "/queue", sinceUtc: hoursAgo(2),
        data: { id: "t1", clientId: "qb", clientName: "qBittorrent", title: "Sintel (2010)", releaseName: "Sintel.2010.1080p", status: "stalled", errorMessage: null, healthFindings: null, addedUtc: hoursAgo(2) }
      }),
      serverRow({
        id: "no-release:movie:m1", kind: "no-release", section: "media", title: "Obscure Film (1971)", to: "/movies/m1", sinceUtc: hoursAgo(5),
        data: { mediaType: "movie", id: "m1", title: "Obscure Film", year: 1971, libraryId: "lib", lastSearchUtc: hoursAgo(1), missingSinceUtc: hoursAgo(5), reason: null }
      }),
      serverRow({
        id: "health:machine:sleep", kind: "health:machine", title: "Computer sleep", text: "This computer goes to sleep after 30 minutes on mains power.", sinceUtc: hoursAgo(1),
        data: { id: "machine:sleep", category: "machine", name: "Computer sleep", link: "/system" }
      })
    ]);
    return { ...attention, total: summary.count, summary };
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

describe("each button on a bell row sends the request it says it will (#1098)", () => {
  afterEach(() => {
    fetchPageItems.mockReset();
    queryClient.clear();
    navigate.mockReset();
    authedFetch.mockClear();
  });

  const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();

  function oneOfEach(): AttentionSnapshot {
    const summary = summaryOf([
      serverRow({ id: "import:j9", kind: "import", section: "media", tone: "bad", title: "Heat (1995)", text: "The disk was full.", to: "/queue", data: { id: "j9" } }),
      serverRow({
        id: "handoff:h9", kind: "handoff", section: "media", tone: "bad", title: "Metropolis (1927)", text: "The processor timed out.", to: "/queue",
        data: { id: "h9", releaseName: "Metropolis.1927.1080p", status: "failed", outputPath: null, importJobId: null, outcome: null }
      }),
      serverRow({
        id: "no-release:movie:m9", kind: "no-release", section: "media", title: "Obscure Film (1971)", to: "/movies/m9",
        data: { mediaType: "movie", id: "m9", title: "Obscure Film", year: 1971, libraryId: "lib 1", lastSearchUtc: hoursAgo(1), missingSinceUtc: hoursAgo(5), reason: null }
      }),
      serverRow({ id: "health:system:disk", kind: "health:system", title: "A drive is nearly full", data: { id: "system:disk", category: "system", name: "A drive is nearly full", link: "/system" } })
    ]);
    return { ...attention, total: summary.count, summary };
  }

  /** Three old no-release titles are one pile, whose one button opens the wanted list. */
  function oldPile(): AttentionSnapshot {
    const rows = [1, 2, 3].map((n) => serverRow({
      id: `no-release:movie:old${n}`, kind: "no-release", section: "media", title: `Old Film ${n} (1970)`, to: `/movies/old${n}`, sinceUtc: hoursAgo(48),
      data: { mediaType: "movie", id: `old${n}`, title: `Old Film ${n}`, year: 1970, libraryId: "lib", lastSearchUtc: hoursAgo(1), missingSinceUtc: hoursAgo(48), reason: null }
    }));
    const summary = summaryOf(rows);
    return { ...attention, total: summary.count, summary };
  }

  async function open(snapshot: AttentionSnapshot) {
    fetchPageItems.mockResolvedValue([]);
    const user = userEvent.setup();
    render(<MemoryRouter><NotificationsBell attention={snapshot} /></MemoryRouter>);
    await user.click(screen.getByRole("button", { name: /notifications/i }));
    return { user, dialog: await screen.findByRole("dialog") };
  }

  const lastCall = () => {
    const [path, init] = authedFetch.mock.calls.at(-1) as [string, RequestInit];
    return { path, method: init?.method };
  };

  it("Try again on a failed import tries that job again", async () => {
    const { user, dialog } = await open(oneOfEach());
    await user.click(within(dialog).getByRole("button", { name: "Try again: failed import 1" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    expect(lastCall()).toEqual({ path: "/api/jobs/j9/retry", method: "POST" });
  });

  it("Retry on a refused hand-off sends the finished download to the processor again", async () => {
    const { user, dialog } = await open(oneOfEach());
    await user.click(within(dialog).getByRole("button", { name: "Retry: Metropolis (1927)" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    expect(lastCall()).toEqual({ path: "/api/integrations/processors/handoffs/h9/retry", method: "POST" });
  });

  it("Search now on a title with no acceptable release searches that film in its own library", async () => {
    const { user, dialog } = await open(oneOfEach());
    await user.click(within(dialog).getByRole("button", { name: "Search now: Obscure Film (1971)" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    expect(lastCall()).toEqual({ path: "/api/movies/m9/search?libraryId=lib%201", method: "POST" });
  });

  it("Open System on a Deluno problem is a link to System and sends nothing", async () => {
    const { user, dialog } = await open(oneOfEach());
    const deluno = within(dialog).getByRole("region", { name: "Deluno" });
    const openSystem = within(deluno).getByRole("link", { name: /Open System/ });
    expect(openSystem).toHaveAttribute("href", "/system");
    await user.click(openSystem);
    expect(authedFetch).not.toHaveBeenCalled();
  });

  it("Open Wanted on a pile of old titles is a link to the wanted list and sends nothing", async () => {
    const { user, dialog } = await open(oldPile());
    expect(within(dialog).getByText("3 titles have no acceptable release")).toBeInTheDocument();
    const openWanted = within(dialog).getByRole("link", { name: /Open Wanted/ });
    expect(openWanted).toHaveAttribute("href", "/search-cycles/missing");
    await user.click(openWanted);
    expect(authedFetch).not.toHaveBeenCalled();
  });
});
