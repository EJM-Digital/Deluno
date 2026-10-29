import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { NotificationPreferences, NotificationWebhookDeliveryItem, NotificationWebhookItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Notifications: Select mode, the select-all checkbox column and the Tags dialog.
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


describe("selecting notification connections", () => {
  it("wires Select mode, the select-all column and the Tags dialog, then clears the selection and reads the list again", async () => {
    loader.webhooks = [hook({ id: "h1", name: "Chat" }), hook({ id: "h2", name: "Phone" })];
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") && String(input) !== "/api/tags/bulk-connections" ? json([]) : json({ updated: 2 }));
    renderPage();
    expect(screen.queryByRole("checkbox", { name: "Select Chat" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    expect(screen.getByRole("button", { name: "Done" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("checkbox", { name: "Select all notification connections" }));
    expect(screen.getByText("2 selected")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Select Chat" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Select Phone" })).toBeChecked();

    fireEvent.click(screen.getByRole("button", { name: "Change tags on the selected notification connections" }));
    expect(await screen.findByText("Change tags: 2 selected notification connections")).toBeInTheDocument();
    fireEvent.change(screen.getByRole("combobox", { name: "How to apply the tags" }), { target: { value: "replace" } });
    fireEvent.click(screen.getByRole("button", { name: "Replace tags" }));

    await waitFor(() => expect(revalidate).toHaveBeenCalledTimes(1));
    const call = vi.mocked(authedFetch).mock.calls.find(([url]) => String(url) === "/api/tags/bulk-connections")!;
    expect(String(call[1]?.body)).toContain("h1");
    expect(String(call[1]?.body)).toContain("h2");
    expect(screen.queryByText("2 selected")).toBeNull();
    expect(screen.getByRole("checkbox", { name: "Select Chat" })).not.toBeChecked();
  });

  it("unticks a single row and shows the count of what is left", () => {
    loader.webhooks = [hook({ id: "h1", name: "Chat" }), hook({ id: "h2", name: "Phone" })];
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select all notification connections" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Phone" }));
    expect(screen.getByText("1 selected")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Select all notification connections" })).not.toBeChecked();
  });
});
