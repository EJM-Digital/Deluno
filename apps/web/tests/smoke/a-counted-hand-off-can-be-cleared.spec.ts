import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #570. In progress counts any hand-off that is not dismissed and carries a
// reason. The drawer used to offer Dismiss only for the endings it called
// failures, so a hand-off that ended "completed" carrying a reason was counted
// for ever with nothing to click. On the rig that was a film removed while Weir
// still had its file: Weir finished anyway, reported the output, and Deluno
// said so — truthfully, and unclearably.
const delivered = {
  id: "handoff-late",
  libraryId: "library-movies",
  mediaType: "movies",
  clientId: "qbittorrent",
  queueItemId: "hash-1",
  releaseName: "Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  sourcePath: "C:\\Downloads-Complete\\Movies\\Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  processorName: "Weir",
  status: "completed",
  outputPath: "C:\\Deluno\\Weir3\\Refined\\Movies\\Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  importJobId: null,
  failureMessage:
    "Weir produced a cleaned file, but the film was removed from Deluno, so Deluno did not import it.",
  createdUtc: "2026-09-22T23:16:00Z",
  updatedUtc: "2026-09-22T23:18:00Z",
  processor: null
};

test("a hand-off In progress is still asking about can be ended, whatever ended it", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  const posted: string[] = [];
  await page.route("**/api/integrations/processors/handoffs?**", async (route) => {
    await route.fulfill({ contentType: "application/json", body: JSON.stringify([delivered]) });
  });
  await page.route("**/api/integrations/processors/handoffs/*/dismiss", async (route) => {
    posted.push(new URL(route.request().url()).pathname);
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ message: "Dismissed." })
    });
  });

  await page.goto("/queue");
  await page.getByRole("row").filter({ hasText: delivered.releaseName }).first().click();
  await expect(page.getByRole("button", { name: "Dismiss" })).toBeVisible();

  await page.getByRole("button", { name: "Dismiss" }).click();

  expect(posted).toEqual([`/api/integrations/processors/handoffs/${delivered.id}/dismiss`]);
});
