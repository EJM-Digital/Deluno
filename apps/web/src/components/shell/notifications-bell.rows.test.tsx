import { render, screen, waitFor, within, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { emptyAttentionSummary } from "../../lib/attention-summary";
import { serverRow, summaryOf } from "../../lib/attention-summary-fixtures";

/** #1329: each kind of row in the bell's panel, pressed, and the request it sends. */

const fetchPageItems = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchPageItems
}));

const authedFetch = vi.hoisted(() => vi.fn((_path: string, _init?: RequestInit) => Promise.resolve(new Response("{}"))));
vi.mock("../../lib/use-auth", () => ({ authedFetch }));

const { NotificationsBell } = await import("./notifications-bell");
const { queryClient } = await import("../../lib/route-cache");

const base: AttentionSnapshot = {
  failedJobs: 0, failedTitleJobs: 0, failedBackgroundJobs: 0, deadLetteredJobs: 0, indexerAlerts: 0, downloadsNeedingAction: 0, failedImportJobs: 0,
  movieWanted: 0, tvWanted: 0, healthProblems: 0, total: 0, summary: emptyAttentionSummary, loading: false
};

const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();

function snapshot(rows = [] as ReturnType<typeof serverRow>[], notices = [] as ReturnType<typeof serverRow>[]): AttentionSnapshot {
  const summary = summaryOf(rows, notices);
  return { ...base, total: summary.count, summary };
}

const row = (id: string, kind: string, title: string, data: Record<string, unknown> | null, over: Record<string, unknown> = {}) =>
  serverRow({ id, kind, section: "media", tone: "bad", title, text: `${title} needs a look.`, to: "/queue", data, ...over } as never);

beforeEach(() => {
  fetchPageItems.mockReset();
  fetchPageItems.mockResolvedValue([]);
  authedFetch.mockReset();
  authedFetch.mockImplementation(() => Promise.resolve(new Response("{}")));
  queryClient.clear();
});
afterEach(() => vi.restoreAllMocks());

async function open(attention: AttentionSnapshot, initialPath = "/") {
  const user = userEvent.setup();
  render(<MemoryRouter initialEntries={[initialPath]}><NotificationsBell attention={attention} /></MemoryRouter>);
  await user.click(screen.getByRole("button", { name: /notifications/i }));
  return { user, dialog: await screen.findByRole("dialog") };
}

const lastCall = () => {
  const [path, init] = authedFetch.mock.calls.at(-1) as [string, RequestInit | undefined];
  return { path, method: init?.method, body: init?.body ? JSON.parse(String(init.body)) : undefined };
};

describe("the bell's media rows, pressed in the panel (#1329)", () => {
  it("while a button's request is held, the row's buttons are disabled; when it answers the summary is read again", async () => {
    let answer: (response: Response) => void = () => undefined;
    authedFetch.mockImplementation(() => new Promise<Response>((resolve) => { answer = resolve; }));
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    const { user, dialog } = await open(snapshot([row("import:j1", "import", "Heat (1995)", { id: "j1" })]));

    const retry = within(dialog).getByRole("button", { name: "Try again: failed import 1" });
    const dismiss = within(dialog).getByRole("button", { name: "Dismiss: failed import 1" });
    expect(retry).toBeEnabled();
    await user.click(retry);
    await waitFor(() => expect(retry).toBeDisabled());
    expect(dismiss).toBeDisabled();
    expect(invalidate).not.toHaveBeenCalled();

    await act(async () => { answer(new Response("{}")); });
    await waitFor(() => expect(invalidate).toHaveBeenCalled());
    await waitFor(() => expect(retry).toBeEnabled());
  });

  it("a failed import row's Try again retries that one job and its Dismiss dismisses it", async () => {
    const { user, dialog } = await open(snapshot([row("import:j7", "import", "Heat (1995)", { id: "j7" })]));

    await user.click(within(dialog).getByRole("button", { name: "Try again: failed import 1" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toMatchObject({ path: "/api/jobs/j7/retry", method: "POST" });

    await user.click(within(dialog).getByRole("button", { name: "Dismiss: failed import 1" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(2));
    expect(lastCall()).toMatchObject({ path: "/api/jobs/j7/dismiss", method: "POST" });
  });

  it("an import case row offers Retry (or Search now when the download vanished, or Review) and Dismiss, each sending its request", async () => {
    const { user, dialog } = await open(snapshot([
      row("recovery:c1", "recovery", "Arrival", { id: "c1", title: "Arrival", failureKind: "processedFileIsLess", mediaType: "movie", detailsJson: JSON.stringify({ DispatchId: "d1" }) }),
      row("recovery:c2", "recovery", "Sintel", { id: "c2", title: "Sintel", failureKind: "vanished-downloading", mediaType: "series", detailsJson: JSON.stringify({ DispatchId: "d2" }) }),
      row("recovery:c3", "recovery", "Dune", { id: "c3", title: "Dune", failureKind: "noVideoStream", mediaType: "movie", detailsJson: null })
    ]));

    await user.click(within(dialog).getByRole("button", { name: "Retry: Arrival" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toMatchObject({ path: "/api/movies/import-recovery/c1/retry", method: "POST" });

    await user.click(within(dialog).getByRole("button", { name: "Search now: Sintel" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(2));
    expect(lastCall()).toMatchObject({ path: "/api/series/import-recovery/c2/retry", method: "POST" });

    await user.click(within(dialog).getByRole("button", { name: "Dismiss: Dune" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(3));
    expect(lastCall()).toMatchObject({ path: "/api/movies/import-recovery/c3/dismiss", method: "POST" });
    expect(within(dialog).getByRole("link", { name: "Review: Dune" })).toHaveAttribute("href", "/queue");
    expect(within(dialog).queryByRole("button", { name: "Retry: Dune" })).not.toBeInTheDocument();
  });

  it("a refused hand-off row sends Retry, Find another (source gone) and Dismiss, and offers no Retry once the file is processed", async () => {
    const handoff = (id: string, over: Record<string, unknown>) => ({ id, releaseName: `Release ${id}`, status: "failed", outputPath: null, importJobId: null, outcome: null, ...over });
    const { user, dialog } = await open(snapshot([
      row("handoff:h1", "handoff", "Metropolis", handoff("h1", {})),
      row("handoff:h2", "handoff", "Nosferatu", handoff("h2", { status: "source-gone" })),
      row("handoff:h3", "handoff", "Sunrise", handoff("h3", { outputPath: "D:/Clean/Sunrise.mkv" }))
    ]));

    await user.click(within(dialog).getByRole("button", { name: "Retry: Metropolis" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toMatchObject({ path: "/api/integrations/processors/handoffs/h1/retry", method: "POST" });

    await user.click(within(dialog).getByRole("button", { name: "Find another: Nosferatu" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(2));
    expect(lastCall()).toMatchObject({ path: "/api/integrations/processors/handoffs/h2/search-again", method: "POST" });
    expect(within(dialog).queryByRole("button", { name: "Retry: Nosferatu" })).not.toBeInTheDocument();

    await user.click(within(dialog).getByRole("button", { name: "Dismiss: Metropolis" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(3));
    expect(lastCall()).toMatchObject({ path: "/api/integrations/processors/handoffs/h1/dismiss", method: "POST" });

    expect(within(dialog).queryByRole("button", { name: "Retry: Sunrise" })).not.toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Dismiss: Sunrise" })).toBeInTheDocument();
  });

  it("a stuck download's Dismiss silences that client's finding for 7 days", async () => {
    const { user, dialog } = await open(snapshot([row("stuck:qb:t1", "stuck", "Sintel (2010)", {
      id: "t1", clientId: "qb", clientName: "qBittorrent", title: "Sintel (2010)", releaseName: "Sintel.2010", status: "stalled", errorMessage: null,
      healthFindings: [{ kind: "stalled", summary: "Not moving." }], addedUtc: hoursAgo(2)
    }, { sinceUtc: hoursAgo(2) })]));

    await user.click(within(dialog).getByRole("button", { name: "Dismiss: Sintel (2010)" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toEqual({ path: "/api/download-clients/qb/queue/t1/health/stalled/ignore", method: "POST", body: { durationDays: 7 } });
  });
});

describe("the bell's Deluno rows, pressed in the panel (#1329)", () => {
  const deluno = (id: string, kind: string, title: string, data: Record<string, unknown> | null, over: Record<string, unknown> = {}) =>
    row(id, kind, title, data, { section: "deluno", to: "/system", ...over });

  it("a connection that is down offers Test again and, for an indexer or download client, Switch off", async () => {
    const { user, dialog } = await open(snapshot([
      deluno("indexer:i1", "connection", "NZBgeek", { id: "i1", name: "NZBgeek" }),
      deluno("client:c1", "connection", "SABnzbd", { id: "c1", name: "SABnzbd" }),
      deluno("processor:p1", "connection", "Weir", { id: "p1", name: "Weir" })
    ]));

    await user.click(within(dialog).getByRole("button", { name: "Test again: NZBgeek" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toMatchObject({ path: "/api/connections/indexer/i1/test", method: "POST" });

    await user.click(within(dialog).getByRole("button", { name: "Switch off: NZBgeek" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(2));
    expect(lastCall()).toEqual({ path: "/api/indexers/i1", method: "PUT", body: { isEnabled: false } });

    await user.click(within(dialog).getByRole("button", { name: "Switch off: SABnzbd" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(3));
    expect(lastCall()).toEqual({ path: "/api/download-clients/c1", method: "PUT", body: { isEnabled: false } });

    await user.click(within(dialog).getByRole("button", { name: "Test again: Weir" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(4));
    expect(lastCall()).toMatchObject({ path: "/api/connections/processor/p1/test", method: "POST" });
    expect(within(dialog).queryByRole("button", { name: "Switch off: Weir" })).not.toBeInTheDocument();
  });

  it("a media server and a request server problem offer Test again for their own kind", async () => {
    const { user, dialog } = await open(snapshot([
      deluno("health:media-server:m1", "health:media-server", "Emby", { id: "media-server:m1", category: "media-server", name: "Emby", link: "/indexers/media-servers" }),
      deluno("health:seerr:r1", "health:seerr", "Seerr", { id: "seerr:r1", category: "seerr", name: "Seerr", link: "/indexers/media-servers" })
    ]));

    await user.click(within(dialog).getByRole("button", { name: "Test again: Emby" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toMatchObject({ path: "/api/connections/mediaServer/m1/test", method: "POST" });

    await user.click(within(dialog).getByRole("button", { name: "Test again: Seerr" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(2));
    expect(lastCall()).toMatchObject({ path: "/api/connections/requests/r1/test", method: "POST" });
  });

  it("the machine rows: Back up now, Snooze 1 day and Snooze 1 week send their requests, and Clean up goes to Transfers", async () => {
    const { user, dialog } = await open(snapshot([
      deluno("health:machine:drive", "health:machine", "Drive C is nearly full", { id: "machine:drive:C", category: "machine", name: "Drive C is nearly full", link: "/system" }),
      deluno("health:machine:reboot", "health:machine", "Restart pending", { id: "machine:reboot", category: "machine", name: "Restart pending", link: "/system" }),
      deluno("health:system:disk-space", "health:system", "Low disk space", { id: "system:disk-space", category: "system", name: "Low disk space", link: "/system" })
    ]));

    await user.click(within(dialog).getByRole("button", { name: "Back up now: Drive C is nearly full" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toMatchObject({ path: "/api/backups", method: "POST" });

    await user.click(within(dialog).getByRole("button", { name: "Snooze 1 day: Restart pending" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(2));
    expect(lastCall()).toEqual({ path: "/api/system/health/machine%3Areboot/snooze", method: "POST", body: { hours: 24 } });

    await user.click(within(dialog).getByRole("button", { name: "Snooze 1 week: Restart pending" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(3));
    expect(lastCall()).toEqual({ path: "/api/system/health/machine%3Areboot/snooze", method: "POST", body: { hours: 168 } });

    expect(within(dialog).getByRole("link", { name: "Clean up: Low disk space" })).toHaveAttribute("href", "/queue");
  });

  it("a 'jobs gave up' row Retries them all, and Review goes to System for background jobs and to Activity's Failed list for title jobs", async () => {
    const { user, dialog } = await open(snapshot([
      deluno("failed-background-jobs", "jobs", "2 background jobs gave up", null),
      deluno("failed-title-jobs", "jobs", "1 job for a title gave up", null, { section: "media" })
    ]));

    await user.click(within(dialog).getAllByRole("button", { name: /^Retry: / })[0]);
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toMatchObject({ path: "/api/jobs/retry-failed", method: "POST" });

    const reviews = within(dialog).getAllByRole("link", { name: /^Review: / });
    const targets = reviews.map((link) => link.getAttribute("href"));
    expect(targets).toContain("/activity?status=failed");
    expect(targets.some((href) => href !== "/activity?status=failed" && href?.startsWith("/system"))).toBe(true);
  });
});

describe("the bell's update rows and its links (#1329)", () => {
  it("'Open Updates' goes to Updates; a connected app's 'Open' goes to that connection and its Dismiss drops that version", async () => {
    const { user, dialog } = await open(snapshot([], [
      serverRow({ id: "update", kind: "update", section: "deluno", title: "A new version of Deluno is ready", to: "/system/updates", action: "Open Updates" }),
      serverRow({ id: "connection-update:c9", kind: "connection-update", section: "deluno", title: "qBittorrent has an update", to: "/indexers/download-clients?connection=c9", data: { kind: "downloadClient", id: "c9" } })
    ]));

    expect(within(dialog).getByRole("link", { name: /Open Updates/ })).toHaveAttribute("href", "/system/updates");
    expect(within(dialog).getByRole("link", { name: /^Open: / })).toHaveAttribute("href", "/indexers/download-clients?connection=c9");
    await user.click(within(dialog).getByRole("button", { name: "Dismiss: qBittorrent has an update" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledTimes(1));
    expect(lastCall()).toMatchObject({ path: "/api/connections/downloadClient/c9/version/dismiss", method: "POST" });
  });

  it("a row's title link goes to its screen and the panel closes behind it", async () => {
    const { user, dialog } = await open(snapshot([
      row("health:system:backup", "health:system", "A drive is nearly full", { id: "system:backup", category: "system", name: "A drive is nearly full", link: "/system" }, { section: "deluno", to: "/system" })
    ]));

    await user.click(within(dialog).getByRole("link", { name: "A drive is nearly full" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });
});

describe("the bell's Recent list while it loads and when nothing matters (#1329)", () => {
  it("says Loading before the first read, and hides the Recent heading when no event matters", async () => {
    let release: (value: unknown[]) => void = () => undefined;
    fetchPageItems.mockImplementation(() => new Promise((resolve) => { release = resolve; }));
    const { dialog } = await open(snapshot());

    expect(within(dialog).getByText("Loading…")).toBeInTheDocument();
    expect(within(dialog).getByText("Recent")).toBeInTheDocument();
    expect(fetchPageItems).toHaveBeenCalledWith("/api/activity?pageSize=400");

    // Only routine job noise came back: nothing matters, so there is no Recent list at all.
    await act(async () => {
      release([{ id: "n1", category: "job.queued", message: "Added a media read", severity: "info", detailsJson: null, relatedJobId: "j1", relatedEntityType: null, relatedEntityId: null, createdUtc: hoursAgo(1) }]);
    });
    await waitFor(() => expect(within(dialog).queryByText("Loading…")).not.toBeInTheDocument());
    expect(within(dialog).queryByText("Recent")).not.toBeInTheDocument();
  });

  it("when reading the Recent list fails the panel says nothing: no error text and no retry", async () => {
    fetchPageItems.mockRejectedValue(new Error("offline"));
    const { dialog } = await open(snapshot());

    await waitFor(() => expect(within(dialog).queryByText("Loading…")).not.toBeInTheDocument());
    expect(dialog.textContent).not.toMatch(/offline|could not|error|try again/i);
    expect(within(dialog).queryByRole("button", { name: /retry|try again/i })).not.toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Show all activity" })).toBeInTheDocument();
  });

  it("anchors the panel under the bell at 608 px, and pulls it inside a phone screen with 16 px margins", async () => {
    const original = window.innerWidth;
    try {
      Object.defineProperty(window, "innerWidth", { configurable: true, value: 390 });
      const { dialog } = await open(snapshot());
      expect(dialog.style.width).toBe("358px");
      expect(dialog.style.position).toBe("fixed");
      expect(Number.parseFloat(dialog.style.right)).toBeGreaterThanOrEqual(16);
      expect(390 - Number.parseFloat(dialog.style.right) - Number.parseFloat(dialog.style.width)).toBeGreaterThanOrEqual(16);
    } finally {
      Object.defineProperty(window, "innerWidth", { configurable: true, value: original });
    }
  });

  it("is 608 px wide when the screen has room", async () => {
    const original = window.innerWidth;
    try {
      Object.defineProperty(window, "innerWidth", { configurable: true, value: 1600 });
      const { dialog } = await open(snapshot());
      expect(dialog.style.width).toBe("608px");
    } finally {
      Object.defineProperty(window, "innerWidth", { configurable: true, value: original });
    }
  });
});
