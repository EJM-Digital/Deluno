import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// Before the soak the rig's sidebar said "2 failed jobs" and sent the owner to
// Activity, which said "none failing" with Retry disabled: the failures sat at
// positions 234 and 326 and Activity read the latest 24. Here the failure is
// likewise absent from the latest jobs and only answered when asked for by name.
const buried = {
  id: "job-buried-failure",
  jobType: "filesystem.import.execute",
  label: "Import a download",
  source: "processor",
  status: "dead-letter",
  payloadJson: "{}",
  attempts: 3,
  createdUtc: "2026-09-17T00:12:38Z",
  scheduledUtc: "2026-09-17T03:41:06Z",
  startedUtc: "2026-09-17T03:39:36Z",
  completedUtc: "2026-09-17T03:41:07Z",
  leasedUntilUtc: null,
  workerId: "worker-import.files",
  lastError: "The refined file is not a readable video.",
  relatedEntityType: "movie",
  relatedEntityId: null,
  idempotencyKey: null,
  dedupeKey: null,
  maxAttempts: 3,
  lastAttemptUtc: "2026-09-17T03:41:07Z",
  nextAttemptUtc: null
};

const latest = Array.from({ length: 24 }, (_, index) => ({
  ...buried,
  id: `job-probe-${index}`,
  jobType: "library.media.probe",
  label: "Read what your files are",
  source: "system",
  status: "completed",
  attempts: 1,
  lastError: null
}));

test("a failed job buried under newer work is listed on Activity and can be dismissed there", async ({ page }) => {
  await authenticateAndNavigate(page, "/");

  let dismissed = false;
  await page.route("**/api/jobs?**", async (route) => {
    const failedOnly = new URL(route.request().url()).searchParams.get("failed") === "true";
    const items = failedOnly ? (dismissed ? [] : [buried]) : latest;
    await route.fulfill({ contentType: "application/json", body: JSON.stringify({ items, nextPageToken: null, hasMore: false }) });
  });
  await page.route(`**/api/jobs/${buried.id}/dismiss`, async (route) => {
    dismissed = true;
    await route.fulfill({ contentType: "application/json", body: JSON.stringify(buried) });
  });

  await page.goto("/activity?section=jobs");

  await expect(page.getByRole("button", { name: "Retry 1 job" })).toBeEnabled();
  const failedCard = page.getByRole("region", { name: "Failed jobs" }).or(page.locator("section", { hasText: "Failed jobs" })).first();
  await expect(failedCard.getByText("The refined file is not a readable video.")).toBeVisible();

  await failedCard.getByText("The refined file is not a readable video.").click();
  await page.getByRole("dialog").getByRole("button", { name: "Dismiss", exact: true }).click();

  await expect(page.getByText("The refined file is not a readable video.")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Retry failed jobs" })).toBeDisabled();
});

// The rig's last uncleared item: a failed download. The API could archive it;
// no screen offered to, so Transfers counted it as needing a person for a week.
test("a failed download on Transfers can be dismissed from its details", async ({ page }) => {
  await authenticateAndNavigate(page, "/");

  const failedDownload = {
    id: "dispatch-failed-import",
    libraryId: "movie-library",
    mediaType: "movies",
    entityType: "movie",
    entityId: "movie-42",
    releaseName: "The.Terror.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
    indexerName: "Proof Torznab",
    downloadClientId: "client-1",
    downloadClientName: "qBittorrent",
    status: "sent",
    notesJson: null,
    createdUtc: "2026-09-17T03:38:00Z",
    grabStatus: "succeeded",
    importStatus: "failed",
    importFailureMessage: "The refined file is not a readable video.",
    attemptCount: 1
  };
  let archived = false;
  let archiveReason: string | null = null;
  await page.route("**/api/download-dispatches?**", async (route) => {
    await route.fulfill({ contentType: "application/json", body: JSON.stringify({ items: archived ? [] : [failedDownload], nextPageToken: null, hasMore: false }) });
  });
  await page.route(`**/api/v1/download-dispatches/${failedDownload.id}**`, async (route) => {
    if (route.request().method() === "DELETE") {
      archived = true;
      archiveReason = new URL(route.request().url()).searchParams.get("reason");
      await route.fulfill({ status: 204 });
      return;
    }
    await route.fulfill({ contentType: "application/json", body: JSON.stringify({ dispatch: failedDownload, timeline: [] }) });
  });

  await page.goto("/queue");
  await page.getByRole("row").filter({ hasText: "Open transfer details" }).filter({ hasText: failedDownload.releaseName }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Dismiss", exact: true }).click();

  await expect(page.getByRole("row").filter({ hasText: "Open transfer details" })).toHaveCount(0);
  expect(archiveReason).toBe("dismissed");
});
