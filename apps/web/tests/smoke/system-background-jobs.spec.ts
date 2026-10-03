import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// System's Background jobs are the jobs that are not about a title (#1114). A stopped one is listed
// however old it is, and can be dismissed from there; a job about a title is left to Activity.
const base = {
  jobType: "library.metadata.refresh",
  source: "schedule",
  sourceLabel: "The schedule",
  payloadJson: "{}",
  attempts: 3,
  createdUtc: "2026-10-01T00:12:38Z",
  scheduledUtc: "2026-10-01T03:41:06Z",
  startedUtc: "2026-10-01T03:39:36Z",
  completedUtc: "2026-10-01T03:41:07Z",
  leasedUntilUtc: null,
  workerId: "worker-metadata",
  relatedEntityType: null,
  relatedEntityId: null,
  idempotencyKey: null,
  dedupeKey: null,
  maxAttempts: 3,
  lastAttemptUtc: "2026-10-01T03:41:07Z",
  nextAttemptUtc: null
};
const stopped = { ...base, id: "job-background-failure", label: "Refresh all metadata", status: "dead-letter", lastError: "The metadata source did not answer." };
const aboutATitle = { ...base, id: "job-title", label: "Search for Sintel", status: "queued", relatedEntityType: "movie", relatedEntityId: "movie-1", lastError: null };

test("a stopped background job is listed on System and can be dismissed there; a title's job is not", async ({ page }) => {
  await authenticateAndNavigate(page, "/");

  let dismissed = false;
  await page.route("**/api/jobs?**", async (route) => {
    const failedOnly = new URL(route.request().url()).searchParams.get("failed") === "true";
    const items = failedOnly ? (dismissed ? [] : [stopped]) : [aboutATitle];
    await route.fulfill({ contentType: "application/json", body: JSON.stringify({ items, nextPageToken: null, hasMore: false }) });
  });
  await page.route(`**/api/jobs/${stopped.id}/dismiss`, async (route) => {
    dismissed = true;
    await route.fulfill({ contentType: "application/json", body: JSON.stringify(stopped) });
  });

  await page.goto("/system");

  await expect(page.getByRole("button", { name: "Retry failed jobs" })).toBeEnabled();
  await expect(page.getByRole("region", { name: "Background jobs" }).getByText("Refresh all metadata")).toBeVisible();
  await expect(page.getByText("Search for Sintel")).toHaveCount(0);

  await page.getByText("Refresh all metadata").click();
  await expect(page.getByRole("dialog").getByText("The metadata source did not answer.")).toBeVisible();
  await page.getByRole("dialog").getByRole("button", { name: "Dismiss", exact: true }).click();

  // Read inside the card: anything else on the page that names the job (the toast saying it was dismissed, or the
  // shared CI backend's own metadata refresh) is not the row this test dismissed.
  await expect(page.getByRole("region", { name: "Background jobs" }).getByText("Refresh all metadata")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Retry failed jobs" })).toHaveCount(0);
});
