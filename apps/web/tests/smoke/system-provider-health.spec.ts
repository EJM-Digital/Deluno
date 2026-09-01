import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test("System Health attributes a provider failure with its recovery state", async ({ page }) => {
  await authenticateAndNavigate(page, "/");

  await page.route("**/api/download-clients", async (route) => {
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify([{
        id: "provider-health-client-42",
        name: "Health test qBittorrent",
        protocol: "qbittorrent",
        host: "192.168.1.142",
        port: 8080,
        username: null,
        endpointUrl: "http://192.168.1.142:8080",
        moviesCategory: "movies",
        tvCategory: "tv",
        categoryTemplate: null,
        priority: 1,
        isEnabled: true,
        healthStatus: "unreachable",
        lastHealthMessage: "Deluno could not contact qBittorrent.",
        lastHealthLatencyMs: 1200,
        lastHealthTestUtc: "2026-09-01T05:00:00Z",
        createdUtc: "2026-09-01T04:00:00Z",
        updatedUtc: "2026-09-01T05:00:00Z",
        lastHealthFailure: {
          serviceType: "download-client",
          serviceId: "provider-health-client-42",
          serviceName: "Health test qBittorrent",
          operation: "queue.refresh",
          kind: "Unavailable",
          retryState: "RetryScheduled",
          message: "The client refused the connection.",
          code: "ECONNREFUSED",
          httpStatus: null,
          upstreamDetail: "Connection refused by 192.168.1.142:8080.",
          externalId: null,
          retryAfterUtc: "2026-09-01T05:05:00Z",
          attempts: 2,
          isTransient: true,
          legacyCategory: "connection",
          summary: "qBittorrent did not accept Deluno's connection.",
          nextAction: "Check that qBittorrent is running and reachable, then test the connection again."
        }
      }])
    });
  });

  await page.goto("/system");
  await page.getByRole("row").filter({ hasText: "Health test qBittorrent" }).click();

  const drawer = page.getByRole("dialog", { name: "Health test qBittorrent" });
  await expect(drawer).toBeVisible();
  await expect(drawer.getByText("qBittorrent did not accept Deluno's connection.", { exact: true })).toBeVisible();
  await expect(drawer.getByText("Connection refused by 192.168.1.142:8080.", { exact: false })).toBeVisible();
  await expect(drawer.getByText("Retry scheduled", { exact: true })).toBeVisible();
  await expect(drawer.getByText("Deluno will retry automatically when the next eligible time arrives.", { exact: true })).toBeVisible();
  await expect(drawer.getByText("Check that qBittorrent is running and reachable, then test the connection again.", { exact: true })).toBeVisible();
  await expect(drawer.getByText("Service", { exact: true })).toBeVisible();
  await expect(drawer.getByText("Queue refresh", { exact: true })).toBeVisible();
  await expect(drawer.getByText("Next eligible", { exact: true })).toBeVisible();
});
