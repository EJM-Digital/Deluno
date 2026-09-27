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
