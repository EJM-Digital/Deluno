import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #566. Deluno waits while a processor says "queued", however long that takes,
// and gives up only after the library's limit — which restarts when the
// processor does. On the rig four hand-offs sat at "queued" for two days for
// work Weir no longer had, and no screen could end one: retry, import original
// and dismiss are all offered once a hand-off has ended. A person can now say
// stop, from the hand-off's own drawer.
const waiting = {
  id: "handoff-stuck",
  libraryId: "library-tv",
  mediaType: "tv",
  clientId: "sabnzbd",
  queueItemId: "nzo-1",
  releaseName: "Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  sourcePath: "C:\\Downloads-Complete\\TV\\Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
  processorName: "Weir",
  status: "submitted",
  outputPath: null,
  importJobId: null,
  failureMessage: null,
  createdUtc: "2026-09-21T00:16:40Z",
  updatedUtc: "2026-09-21T00:16:40Z",
  processor: {
    state: "queued",
    message: null,
    queuePosition: null,
    scheduledForUtc: null,
    lastChangedUtc: "2026-09-21T00:16:40Z",
    checkedUtc: "2026-09-22T21:18:44Z",
    unreachableSinceUtc: null,
    answeringSinceUtc: "2026-09-21T00:17:40Z"
  }
};

async function showHandoff(page: Page, answer: { status: number; message: string }) {
  const posted: string[] = [];
  await page.route("**/api/integrations/processors/handoffs?**", async (route) => {
    await route.fulfill({ contentType: "application/json", body: JSON.stringify([waiting]) });
  });
  await page.route("**/api/integrations/processors/handoffs/*/stop-waiting", async (route) => {
    posted.push(new URL(route.request().url()).pathname);
    await route.fulfill({ status: answer.status, contentType: "application/json", body: JSON.stringify({ message: answer.message }) });
  });
  return posted;
}

test("a hand-off still with the processor can be ended from its drawer", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  const posted = await showHandoff(page, {
    status: 200,
    message: "You stopped waiting for Weir, and it was asked to drop Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO."
  });

  await page.goto("/queue");
  await page.getByRole("row").filter({ hasText: waiting.releaseName }).first().click();
  await expect(page.getByRole("button", { name: "Stop waiting" })).toBeVisible();

  await page.getByRole("button", { name: "Stop waiting" }).click();

  await expect(page.getByText("You stopped waiting for Weir")).toBeVisible();
  expect(posted).toEqual([`/api/integrations/processors/handoffs/${waiting.id}/stop-waiting`]);
});

test("a processor that has started work is not interrupted, and says so", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  await showHandoff(page, {
    status: 409,
    message: "Weir has started work on Pioneer.One.S01E01.1080p.WEB-DL.DDP5.1.H.264-DELUNO, so Deluno will not interrupt it."
  });

  await page.goto("/queue");
  await page.getByRole("row").filter({ hasText: waiting.releaseName }).first().click();
  await page.getByRole("button", { name: "Stop waiting" }).click();

  await expect(page.getByText("has started work on")).toBeVisible();
});
