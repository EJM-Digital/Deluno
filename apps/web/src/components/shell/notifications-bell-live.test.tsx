import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ActivityEventItem } from "../../lib/api";
import type { AttentionSnapshot } from "../../lib/use-attention";
import { emptyAttentionSummary } from "../../lib/attention-summary";

const fetchPageItems = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchPageItems
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

const pushed = vi.hoisted(() => ({ handlers: [] as Array<(event: unknown) => void> }));
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Dashboard: "dashboard", Activity: "activity", Queue: "queue", Live: "live" },
  useSignalREvent: (event: string, _subject: unknown, handler: (event: unknown) => void) => {
    if (event === "ActivityEventAdded") pushed.handlers.push(handler);
  }
}));

const { NotificationsBell } = await import("./notifications-bell");
const { queryClient } = await import("../../lib/route-cache");

const attention: AttentionSnapshot = {
  failedJobs: 0, failedTitleJobs: 0, failedBackgroundJobs: 0, deadLetteredJobs: 0, indexerAlerts: 0, downloadsNeedingAction: 0,
  failedImportJobs: 0, movieWanted: 0, tvWanted: 0, healthProblems: 0, total: 0, summary: emptyAttentionSummary, loading: false
};

const older: ActivityEventItem = {
  id: "evt-1", category: "filesystem.import.completed", message: "Imported The Oldest Film", severity: "info", detailsJson: null,
  relatedJobId: null, relatedEntityType: null, relatedEntityId: null, createdUtc: "2026-09-27T12:01:00Z"
};

function push(message: string, id: string) {
  act(() => {
    pushed.handlers.at(-1)?.({
      id, message, category: "filesystem.import.completed", severity: "success", createdUtc: "2026-10-08T09:00:00Z",
      relatedEntityType: "movie", relatedEntityId: "m9", title: "The Newest Film"
    });
  });
}

describe("the bell's Recent list stays current whether the panel is open or not (#1179)", () => {
  afterEach(() => {
    fetchPageItems.mockReset();
    queryClient.clear();
    pushed.handlers = [];
  });

  it("takes a pushed event while closed and shows it, with no second read, when opened again", async () => {
    fetchPageItems.mockResolvedValue([older]);
    const user = userEvent.setup();
    render(<MemoryRouter><NotificationsBell attention={attention} /></MemoryRouter>);

    // Opened once: the list is read.
    await user.click(screen.getByRole("button", { name: /notifications/i }));
    await screen.findByText("Imported The Oldest Film");
    await user.click(screen.getByRole("button", { name: /notifications/i }));
    await waitFor(() => expect(screen.queryByText("Imported The Oldest Film")).toBeNull());

    // Closed: a new event arrives.
    push("Imported The Newest Film", "evt-9");

    await user.click(screen.getByRole("button", { name: /notifications/i }));
    const list = await screen.findByText("Imported The Newest Film");
    expect(list).toBeTruthy();
    const recent = within(screen.getByRole("dialog", { hidden: true }));
    expect(recent.getByText("Imported The Oldest Film")).toBeTruthy();
    expect(fetchPageItems).toHaveBeenCalledTimes(1);
  });

  it("shows a pushed event at once while the panel is open", async () => {
    fetchPageItems.mockResolvedValue([older]);
    const user = userEvent.setup();
    render(<MemoryRouter><NotificationsBell attention={attention} /></MemoryRouter>);
    await user.click(screen.getByRole("button", { name: /notifications/i }));
    await screen.findByText("Imported The Oldest Film");

    push("Imported The Newest Film", "evt-9");

    expect(await screen.findByText("Imported The Newest Film")).toBeTruthy();
    expect(fetchPageItems).toHaveBeenCalledTimes(1);
  });
});
