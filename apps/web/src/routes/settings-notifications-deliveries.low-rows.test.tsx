import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { NotificationPreferences, NotificationWebhookDeliveryItem, NotificationWebhookItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * The Delivery history card of Settings > Notifications: its heading, empty state and rows.
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", () => ({ toast: { success: (...a: unknown[]) => toastSuccess(...a), error: (...a: unknown[]) => toastError(...a) } }));

const preferences: NotificationPreferences = {
  importCompletedEnabled: true, importFailedEnabled: true, providerHealthChangedEnabled: true, upgradeEnabled: false, renameEnabled: false,
  fileDeletedEnabled: false, manualInteractionRequiredEnabled: false, grabEnabled: false, addedEnabled: true, updateEnabled: false,
  stalledDownloadHandledEnabled: false, queueItemRemovedEnabled: false, orphanedDownloadRemovedEnabled: true, forcedImportEnabled: false,
  strikesPurgedEnabled: false, cleanupClearedEnabled: false, unmatchedImportGivenUpEnabled: false
};

const hook = (over: Partial<NotificationWebhookItem>): NotificationWebhookItem => ({
  id: "h1", name: "Family chat", url: "https://hooks.example.test/a", eventFilters: "grab,import", isEnabled: true, provider: "generic",
  configJson: null, tags: "", includeHealthWarnings: true, lastFiredUtc: null, lastError: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z",
  ...over
} as NotificationWebhookItem);

const loader = { settings: { ...emptyPlatformSettingsSnapshot, enableNotifications: true }, webhooks: [] as NotificationWebhookItem[], deliveries: [] as NotificationWebhookDeliveryItem[], preferences };
const revalidate = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loader,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsNotificationsPage } = await import("./settings-notifications-page");
const renderPage = () => render(<MemoryRouter><SettingsNotificationsPage /></MemoryRouter>);
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const putCalls = () => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "PUT");

beforeEach(() => {
  loader.webhooks = [];
  loader.deliveries = [];
  loader.settings = { ...emptyPlatformSettingsSnapshot, enableNotifications: true };
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : json({ id: "h9" }));
  toastSuccess.mockReset();
  toastError.mockReset();
});
afterEach(() => localStorage.clear());


const delivery = (id: string, status: string, over: Partial<NotificationWebhookDeliveryItem> = {}): NotificationWebhookDeliveryItem => ({
  id, webhookId: "h1", eventCategory: "import", title: `Event ${id}`, status, attemptCount: 2, maxAttempts: 5, nextAttemptUtc: null, lastAttemptUtc: null,
  lastStatusCode: 500, lastError: null, createdUtc: "2026-10-03T00:00:00Z", updatedUtc: "2026-10-03T00:00:00Z", ...over
} as NotificationWebhookDeliveryItem);

describe("Delivery history card", () => {
  it("says there is no delivery history yet, with the reason, and no count, when there are no attempts", () => {
    renderPage();
    expect(screen.getByText("No delivery history yet")).toBeInTheDocument();
    expect(screen.getByText("Deluno will keep a bounded record after the first matching event or test.")).toBeInTheDocument();
    expect(screen.queryByText(/recent delivery attempts/)).toBeNull();
  });

  it("counts the attempts in its heading and says failures remain replayable", () => {
    loader.webhooks = [hook({ id: "h1", name: "Chat" })];
    loader.deliveries = [delivery("a", "delivered"), delivery("b", "dead-letter"), delivery("c", "retrying")];
    renderPage();
    expect(screen.getByText("3 recent delivery attempts · failures remain replayable")).toBeInTheDocument();
    expect(screen.queryByText("No delivery history yet")).toBeNull();
  });

  it("shows each row's event and category, webhook name, attempts out of the maximum with the HTTP code, the status chip and the error", () => {
    loader.webhooks = [hook({ id: "h1", name: "Chat" })];
    loader.deliveries = [
      delivery("dead", "dead-letter", { lastError: "The webhook answered 500." }),
      delivery("ok", "delivered", { attemptCount: 1, lastStatusCode: 204, lastAttemptUtc: new Date(Date.now() - 5 * 60_000).toISOString() }),
      delivery("gone", "queued", { webhookId: "removed-hook", lastStatusCode: null, attemptCount: 0 })
    ];
    renderPage();
    const rows = (title: string) => {
      let node = screen.getByText(title) as HTMLElement;
      // The row is the largest element that holds this event and not yet another one.
      while (node.parentElement && (node.parentElement.textContent ?? "").match(/Event/g)!.length === 1) node = node.parentElement;
      return node;
    };
    const dead = within(rows("Event dead"));
    expect(dead.getByText("import")).toBeInTheDocument();
    expect(dead.getAllByText("Chat").length).toBeGreaterThan(0);
    expect(dead.getByText("2/5")).toBeInTheDocument();
    expect(dead.getByText("HTTP 500")).toBeInTheDocument();
    expect(dead.getByText("dead-letter")).toBeInTheDocument();
    expect(dead.getByText("The webhook answered 500.")).toBeInTheDocument();
    const ok = within(rows("Event ok"));
    expect(ok.getByText("1/5")).toBeInTheDocument();
    expect(ok.getByText("HTTP 204")).toBeInTheDocument();
    expect(ok.getByText("delivered")).toBeInTheDocument();
    expect(ok.getByText("5 min ago")).toBeInTheDocument();
    const gone = within(rows("Event gone"));
    expect(gone.getByText("removed-hook")).toBeInTheDocument();
    expect(gone.getByText("0/5")).toBeInTheDocument();
    expect(gone.queryByText(/HTTP/)).toBeNull();
  });
});
