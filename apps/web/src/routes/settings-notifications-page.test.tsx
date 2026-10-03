import { render, screen, within } from "@testing-library/react";
import { fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { NotificationPreferences, NotificationWebhookDeliveryItem, NotificationWebhookItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * Notification preference switches that gate something outbound: import,
 * lifecycle and recovery rows (#856, #858, #965, #966). The switches that did
 * nothing were removed (#1116).
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const webhooks: NotificationWebhookItem[] = [];
const deliveries: NotificationWebhookDeliveryItem[] = [];
const preferences: NotificationPreferences = {
  importCompletedEnabled: true,
  importFailedEnabled: true,
  providerHealthChangedEnabled: true,
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
  unmatchedImportGivenUpEnabled: false
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

describe("import notification preferences (#965)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows both import rows and none of the removed do-nothing switches", () => {
    renderPage();

    expect(screen.getByText("Import completed")).toBeTruthy();
    expect(screen.getByText("Import failed")).toBeTruthy();
    for (const gone of ["Search completed", "Download started", "Download progress", "Download completed", "Import started", "Automation error", "System warning", "Subtitle downloaded", "Subtitle upgraded", "Subtitle search failed"]) {
      expect(screen.queryByText(gone)).toBeNull();
    }

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

describe("Delivery history columns (#1119)", () => {
  afterEach(() => {
    deliveries.length = 0;
    localStorage.clear();
  });

  it("sorts by a column header and offers Reset columns, like every other list", () => {
    const delivery = (id: string, title: string, attemptCount: number): NotificationWebhookDeliveryItem => ({
      id,
      webhookId: "hook",
      eventCategory: "import",
      title,
      status: "delivered",
      attemptCount,
      maxAttempts: 5,
      nextAttemptUtc: null,
      lastAttemptUtc: null,
      lastStatusCode: 200,
      lastError: null,
      createdUtc: "2026-10-03T00:00:00Z",
      updatedUtc: "2026-10-03T00:00:00Z"
    });
    deliveries.push(delivery("b", "Beta imported", 1), delivery("a", "Alpha imported", 3));
    renderPage();

    const eventNames = () => screen.getAllByText(/^(Alpha|Beta) imported$/).map((node) => node.textContent);
    expect(eventNames()).toEqual(["Beta imported", "Alpha imported"]);

    // The three preference lists above also have an Event column; Delivery history is the last one on the page.
    const header = screen.getAllByRole("columnheader").filter((node) => node.getAttribute("data-col-id") === "event").at(-1)!;
    fireEvent.click(within(header).getByRole("button"));
    expect(header.getAttribute("aria-sort")).toBe("ascending");
    expect(eventNames()).toEqual(["Alpha imported", "Beta imported"]);

    expect(screen.getAllByRole("button", { name: "Columns" }).length).toBeGreaterThan(0);
  });
});
