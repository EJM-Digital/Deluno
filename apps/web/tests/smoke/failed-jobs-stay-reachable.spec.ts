import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// Before the soak the rig's sidebar said "2 failed jobs" and sent the owner to
// Activity, which said "none failing" with Retry disabled: the failures sat at
// positions 234 and 326 and Activity read the latest 24. Here the failure is
// likewise absent from the latest jobs and only answered when asked for by name.
const buried = {
  id: "job-buried-failure",
  jobType: "filesystem.import.execute",
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

  await page.goto("/activity");

  await expect(page.getByRole("button", { name: "Retry 1 job" })).toBeEnabled();
  const failedCard = page.getByRole("region", { name: "Failed jobs" }).or(page.locator("section", { hasText: "Failed jobs" })).first();
  await expect(failedCard.getByText("The refined file is not a readable video.")).toBeVisible();

  await failedCard.getByText("The refined file is not a readable video.").click();
  await page.getByRole("dialog").getByRole("button", { name: "Dismiss", exact: true }).click();

  await expect(page.getByText("The refined file is not a readable video.")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Retry failed jobs" })).toBeDisabled();
});
