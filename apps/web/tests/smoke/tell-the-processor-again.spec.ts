import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #580. Deluno told a processor what became of a file once, and when that did
// not land it never said it again: on the rig Weir 3.2.0 refused one it should
// have taken, and the processor kept its copy for good. Deluno now keeps saying
// it until it is heard, and the hand-off's drawer shows how that stands and
// lets a person say it again.
const handoff = {
  id: "handoff-told",
  libraryId: "library-movies",
  mediaType: "movies",
  clientId: "qbittorrent",
  queueItemId: "hash-1",
  releaseName: "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  sourcePath: "C:\\Downloads-Complete\\Movies\\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  processorName: "Weir",
  status: "completed",
  outputPath: "C:\\Weir\\Refined\\Movies\\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  importJobId: "job-1",
  failureMessage: null,
  createdUtc: "2026-09-23T07:40:00Z",
  updatedUtc: "2026-09-23T07:46:03Z",
  processor: null,
  outcome: {
    handoffId: "handoff-told",
    processorName: "Weir",
    libraryId: "library-movies",
    releaseName: "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
    outcome: "imported",
    importedPath: "X:\\Films\\Tears of Steel (2012)\\Tears of Steel (2012).mkv",
    reason: null,
    occurredUtc: "2026-09-23T07:46:41Z",
    delivery: "refused",
    attempts: 1,
    lastAttemptUtc: "2026-09-23T07:46:41Z",
    nextAttemptUtc: null,
    released: false,
    processorSaid: "Weir has no record of this hand-off.",
    recordedUtc: "2026-09-23T07:46:41Z"
  }
};

test("a report the processor did not take can be said again from the hand-off's drawer", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  const posted: string[] = [];
  await page.route("**/api/integrations/processors/handoffs?**", async (route) => {
    await route.fulfill({ contentType: "application/json", body: JSON.stringify([handoff]) });
  });
  await page.route("**/api/integrations/processors/handoffs/*/outcome/send-again", async (route) => {
    posted.push(new URL(route.request().url()).pathname);
    await route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ message: "Deluno told Weir it has Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO. Nothing for Weir to remove." })
    });
  });

  await page.goto("/queue");
  await page.getByRole("row").filter({ hasText: handoff.releaseName }).first().click();
  await expect(page.getByText("What Weir was told")).toBeVisible();
  await expect(page.getByText("Weir has no record of this hand-off.")).toBeVisible();

  await page.getByRole("button", { name: "Tell Weir again" }).click();

  await expect(page.getByText("Nothing for Weir to remove.")).toBeVisible();
  expect(posted).toEqual([`/api/integrations/processors/handoffs/${handoff.id}/outcome/send-again`]);
});
