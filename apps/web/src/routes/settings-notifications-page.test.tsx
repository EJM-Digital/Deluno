import { render, screen } from "@testing-library/react";
import { fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { NotificationPreferences, NotificationWebhookDeliveryItem, NotificationWebhookItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * #839: none of Deluno's subtitle outcomes (downloaded/upgraded/failed) had
 * a toggle anywhere on screen, even though the backend already gated and
 * fired all three. This covers the new "Subtitle notifications" section
 * reading and saving /api/notification-preferences.
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const webhooks: NotificationWebhookItem[] = [];
const deliveries: NotificationWebhookDeliveryItem[] = [];
const preferences: NotificationPreferences = {
  searchCompletionEnabled: true,
  downloadStartedEnabled: true,
  downloadProgressEnabled: true,
  downloadCompletedEnabled: true,
  importStartedEnabled: true,
  importCompletedEnabled: true,
  importFailedEnabled: true,
  automationErrorEnabled: true,
  systemWarningsEnabled: true,
  providerHealthChangedEnabled: true,
  subtitleDownloadedEnabled: true,
  subtitleUpgradedEnabled: true,
  subtitleFailedEnabled: false,
  upgradeEnabled: false,
  renameEnabled: false,
  fileDeletedEnabled: false,
  manualInteractionRequiredEnabled: false,
  grabEnabled: false,
  addedEnabled: true,
  updateEnabled: false,
  stalledDownloadHandledEnabled: false,
  queueItemRemovedEnabled: false,
  orphanedDownloadRemovedEnabled: true,
  forcedImportEnabled: false,
  strikesPurgedEnabled: false,
  cleanupClearedEnabled: false,
  unmatchedImportGivenUpEnabled: false,
  inAppNotificationsEnabled: true,
  emailNotificationsEnabled: false,
  webhookNotificationsEnabled: false,
  emailAddress: null,
  webhookUrl: null
};

const loaderData = { settings: emptyPlatformSettingsSnapshot, webhooks, deliveries, preferences };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsNotificationsPage } = await import("./settings-notifications-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsNotificationsPage />
    </MemoryRouter>
  );
}

describe("subtitle notification preferences (#839)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows the three subtitle events with their saved on/off state", () => {
    renderPage();

    expect(screen.getByText("Subtitle downloaded")).toBeTruthy();
    expect(screen.getByText("Subtitle upgraded")).toBeTruthy();
    expect(screen.getByText("Subtitle search failed")).toBeTruthy();

    const downloadedSwitch = screen.getByRole("switch", { name: "Subtitle downloaded" });
    const failedSwitch = screen.getByRole("switch", { name: "Subtitle search failed" });
    expect(downloadedSwitch.getAttribute("aria-checked")).toBe("true");
    expect(failedSwitch.getAttribute("aria-checked")).toBe("false");
  });

  it("PUTs the full preferences object with only the toggled field changed", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(null, { status: 200 }));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Subtitle search failed" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/notification-preferences",
      expect.objectContaining({ method: "PUT" })
    ));

    const [, init] = vi.mocked(authedFetch).mock.calls[0];
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.subtitleFailedEnabled).toBe(true);
    expect(body.subtitleDownloadedEnabled).toBe(true);
  });
});

describe("search, download and import notification preferences (#965)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows every search/download/import row with its saved on/off state", () => {
    renderPage();

    expect(screen.getByText("Search completed")).toBeTruthy();
    expect(screen.getByText("Download started")).toBeTruthy();
    expect(screen.getByText("Download progress")).toBeTruthy();
    expect(screen.getByText("Download completed")).toBeTruthy();
    expect(screen.getByText("Import started")).toBeTruthy();
    expect(screen.getByText("Import completed")).toBeTruthy();
    expect(screen.getByText("Import failed")).toBeTruthy();
    expect(screen.getByText("Automation error")).toBeTruthy();
    expect(screen.getByText("System warning")).toBeTruthy();

    expect(screen.getByRole("switch", { name: "Search completed" }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByRole("switch", { name: "Import failed" }).getAttribute("aria-checked")).toBe("true");
  });

  it("PUTs the full preferences object when Import completed is toggled off", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(null, { status: 200 }));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Import completed" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/notification-preferences",
      expect.objectContaining({ method: "PUT" })
    ));

    const [, init] = vi.mocked(authedFetch).mock.calls[0];
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.importCompletedEnabled).toBe(false);
    expect(body.importFailedEnabled).toBe(true);
  });
});

describe("lifecycle notification preferences (#856)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows Grab and Title added alongside the other lifecycle triggers with their saved state", () => {
    renderPage();

    expect(screen.getByText("Grab")).toBeTruthy();
    expect(screen.getByText("Title added")).toBeTruthy();
    expect(screen.getByText("Provider health changed")).toBeTruthy();

    expect(screen.getByRole("switch", { name: "Grab" }).getAttribute("aria-checked")).toBe("false");
    expect(screen.getByRole("switch", { name: "Title added" }).getAttribute("aria-checked")).toBe("true");
  });

  it("PUTs the full preferences object when Grab is toggled on", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(null, { status: 200 }));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Grab" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/notification-preferences",
      expect.objectContaining({ method: "PUT" })
    ));

    const [, init] = vi.mocked(authedFetch).mock.calls[0];
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.grabEnabled).toBe(true);
    expect(body.addedEnabled).toBe(true);
  });
});

describe("recovery and cleanup notification preferences (#858)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("lists Deluno's own strike/cleanup/queue actions under Recovery & cleanup with their saved state", () => {
    renderPage();

    expect(screen.getByText("Stalled/slow download handled")).toBeTruthy();
    expect(screen.getByText("Queue item removed")).toBeTruthy();
    expect(screen.getByText("Orphaned download removed")).toBeTruthy();

    expect(screen.getByRole("switch", { name: "Queue item removed" }).getAttribute("aria-checked")).toBe("false");
    expect(screen.getByRole("switch", { name: "Orphaned download removed" }).getAttribute("aria-checked")).toBe("true");
  });

  it("PUTs the full preferences object when Queue item removed is toggled on", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(null, { status: 200 }));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Queue item removed" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/notification-preferences",
      expect.objectContaining({ method: "PUT" })
    ));

    const [, init] = vi.mocked(authedFetch).mock.calls[0];
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.queueItemRemovedEnabled).toBe(true);
    expect(body.stalledDownloadHandledEnabled).toBe(false);
  });

  it("lists forced re-download, strikes purged, cleanup cleared and unmatched-import given up, off by default (#966)", () => {
    renderPage();

    expect(screen.getByText("Forced re-download")).toBeTruthy();
    expect(screen.getByText("Download-health strikes purged")).toBeTruthy();
    expect(screen.getByText("Cleanup cleared")).toBeTruthy();
    expect(screen.getByText("Unmatched import given up")).toBeTruthy();

    expect(screen.getByRole("switch", { name: "Forced re-download" }).getAttribute("aria-checked")).toBe("false");
    expect(screen.getByRole("switch", { name: "Download-health strikes purged" }).getAttribute("aria-checked")).toBe("false");
    expect(screen.getByRole("switch", { name: "Cleanup cleared" }).getAttribute("aria-checked")).toBe("false");
    expect(screen.getByRole("switch", { name: "Unmatched import given up" }).getAttribute("aria-checked")).toBe("false");
  });

  it("PUTs the full preferences object when Cleanup cleared is toggled on (#966)", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response(null, { status: 200 }));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Cleanup cleared" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/notification-preferences",
      expect.objectContaining({ method: "PUT" })
    ));

    const [, init] = vi.mocked(authedFetch).mock.calls[0];
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.cleanupClearedEnabled).toBe(true);
    expect(body.forcedImportEnabled).toBe(false);
  });
});

describe("per-connection triggers (#1016, #1017)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("a new connection picks triggers one by one, including Deluno update, and the health-warning switch follows Health problems", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => new Response(String(input).startsWith("/api/tags") ? "[]" : JSON.stringify({ id: "w1", eventFilters: "grab" }), { status: 200 }));
    renderPage();

    fireEvent.click(screen.getAllByRole("button", { name: "New webhook" })[0]);
    expect(screen.getByRole("checkbox", { name: /^Deluno update/ })).toBeTruthy();

    // Untick Everything: the ticks fall back to a plain set, without Deluno update.
    fireEvent.click(screen.getByRole("checkbox", { name: /^Everything/ }));
    expect((screen.getByRole("checkbox", { name: /^Deluno update/ }) as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByRole("switch", { name: /Include health warnings/ }));
    fireEvent.click(screen.getByRole("checkbox", { name: /^Health problems/ }));
    expect(screen.queryByRole("switch", { name: /Include health warnings/ })).toBeNull();
    fireEvent.click(screen.getByRole("checkbox", { name: /^Deluno update/ }));
    fireEvent.change(screen.getByPlaceholderText(/https?:\/\//i), { target: { value: "https://hooks.example.test/x" } });
    fireEvent.click(screen.getByRole("button", { name: "Add webhook" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith("/api/notification-webhooks", expect.objectContaining({ method: "POST" })));
    const [, init] = vi.mocked(authedFetch).mock.calls.find(([url, options]) => url === "/api/notification-webhooks" && (options as RequestInit).method === "POST")!;
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body.eventFilters).toBe("grab,import,upgrade,app.update");
    expect(body.includeHealthWarnings).toBe(true);
  });
});
