import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Transfers with a processor hand-off in every state it has, against the real server and the real databases, nothing stubbed
 * (measure 4). Each starting record is written by the same repository calls the work planner makes, through the smoke host's
 * seeding route (TransfersSeedTestEndpoints, only mapped when the smoke process opts in); what the screen then reads, what
 * each drawer offers and every button it presses go through the real routes. The seeding is taken out again at the end, so
 * the specs that follow meet the install as they found it. This file sorts last for that reason.
 */

let libraryId = "";
let libraryFolder = "";

async function seed(page: Page, body: Record<string, unknown>) {
  const reply = await api<{ handoffId: string | null; dispatchId: string | null; importJobId: string | null; recoveryCaseId: string | null }>(
    page, "POST", "/api/testing/transfers/handoff", { libraryId, ...body });
  expect(reply.status, JSON.stringify(reply.body)).toBe(200);
  return reply.body;
}

async function openTransfers(page: Page) {
  await page.goto("/queue");
  await untilQuiet(page);
  await expectNoErrorScreen(page);
}

/** The row of a release, wherever Transfers lists it. */
const rowOf = (page: Page, release: string) => page.getByRole("row").filter({ hasText: release }).first();

test.describe("Transfers, with hand-offs to a processor", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/queue");
    libraryFolder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-handoffs-"));
    const created = await api<{ id: string }>(page, "POST", "/api/libraries", {
      name: "Hand-off films", mediaType: "movies", purpose: "collection", rootPath: libraryFolder, importWorkflow: "refine-before-import", processorName: "Weir"
    });
    expect(created.status, JSON.stringify(created.body)).toBeLessThan(300);
    libraryId = created.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/queue");
    await api(page, "POST", "/api/testing/transfers/clear");
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(libraryFolder, { recursive: true, force: true });
  });

  test.afterEach(async ({ page }) => {
    // Each test leaves the install as it found it; a clear that failed would show in the next test, so it is checked here.
    const cleared = await api(page, "POST", "/api/testing/transfers/clear");
    expect(cleared.status, JSON.stringify(cleared.body)).toBe(204);
  });

  test("a hand-off still with the processor is a row of its own, in the processor's words, and clicking it opens its drawer with the release and 'Weir · submitted'", async ({ page }) => {
    await openScreen(page, "/queue");
    await seed(page, { releaseName: "Click.The.Row.2021.1080p-GRP", status: "submitted", processorState: "queued", queuePosition: 3 });
    await openTransfers(page);

    const row = rowOf(page, "Click.The.Row.2021.1080p-GRP");
    await expect(row).toContainText("Weir: 3rd in queue");
    await expect(row).toContainText("Submitted to connector");
    await row.click();

    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Click.The.Row.2021.1080p-GRP").first()).toBeVisible();
    await expect(drawer.getByText("Weir · submitted")).toBeVisible();
    await expect(drawer.getByText("Processor hand-off")).toBeVisible();
    await expectNoErrorScreen(page);
  });

  test("'Still with the processor' and Stop waiting show for a hand-off that is waiting, submitted, accepted or started, and for no other", async ({ page }) => {
    await openScreen(page, "/queue");
    const withTheProcessor = ["waiting", "submitted", "accepted", "started"];
    const ended = ["failed", "timed-out", "lost", "source-gone"];
    for (const status of [...withTheProcessor, ...ended]) await seed(page, { releaseName: `Hand.Off.${status}.2022.1080p-GRP`, status, failureMessage: ended.includes(status) ? "It did not work out." : null });
    await openTransfers(page);

    for (const status of withTheProcessor) {
      await rowOf(page, `Hand.Off.${status}.2022.1080p-GRP`).click();
      const drawer = page.getByRole("dialog");
      await expect(drawer.getByText("Still with the processor", { exact: false })).toBeVisible();
      await expect(drawer.getByText("Stop waiting for Weir")).toBeVisible();
      await expect(drawer.getByRole("button", { name: "Stop waiting", exact: true })).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(drawer).toHaveCount(0);
    }
    for (const status of ended) {
      await rowOf(page, `Hand.Off.${status}.2022.1080p-GRP`).click();
      const drawer = page.getByRole("dialog");
      await expect(drawer.getByText("Processor hand-off")).toBeVisible();
      await expect(drawer.getByText("Still with the processor", { exact: false })).toHaveCount(0);
      await expect(drawer.getByRole("button", { name: "Stop waiting", exact: true })).toHaveCount(0);
      await page.keyboard.press("Escape");
      await expect(drawer).toHaveCount(0);
    }
  });

  test("Stop waiting asks the server, which says in words that Weir could not be told, and the hand-off leaves the processor's table", async ({ page }) => {
    await openScreen(page, "/queue");
    await seed(page, { releaseName: "Stop.Waiting.2025.1080p-GRP", status: "accepted" });
    await openTransfers(page);

    await rowOf(page, "Stop.Waiting.2025.1080p-GRP").click();
    const answered = page.waitForResponse((response) => response.url().includes("/stop-waiting") && response.request().method() === "POST");
    await page.getByRole("dialog").getByRole("button", { name: "Stop waiting", exact: true }).click();

    const reply = await answered;
    expect(reply.status()).toBe(200);
    const message = ((await reply.json()) as { message: string }).message;
    expect(message).toContain("You stopped waiting for Weir.");
    await expect(page.getByText(message).first()).toBeVisible();
    await expect.poll(async () => (await api<Array<{ id: string; status: string }>>(page, "GET", "/api/integrations/processors/handoffs?take=50")).body.filter((h) => h.status === "accepted").length).toBe(0);
  });

  test("a failed hand-off reads Processing failed, a timed-out one Timed out and one whose download was gone Download gone, the same on Transfers and on the Dashboard", async ({ page }) => {
    await openScreen(page, "/queue");
    await seed(page, { releaseName: "Words.Failed.2023.1080p-GRP", status: "failed", failureMessage: "Weir refused the hand-off." });
    await seed(page, { releaseName: "Words.Timed.2023.1080p-GRP", status: "timed-out", failureMessage: "Weir never answered." });
    await seed(page, { releaseName: "Words.Gone.2023.1080p-GRP", status: "source-gone", failureMessage: "The download was gone before Weir could clean it." });
    const words: Array<[string, string]> = [["Words.Failed", "Processing failed"], ["Words.Timed", "Timed out"], ["Words.Gone", "Download gone"]];

    await openTransfers(page);
    for (const [release, word] of words) {
      await rowOf(page, `${release}.2023.1080p-GRP`).click();
      const drawer = page.getByRole("dialog");
      await expect(drawer.getByText(word, { exact: true }).first()).toBeVisible();
      if (word === "Download gone") await expect(drawer.getByText("Processing failed")).toHaveCount(0);
      await page.keyboard.press("Escape");
      await expect(drawer).toHaveCount(0);
    }

    await page.goto("/");
    await untilQuiet(page);
    for (const [release, word] of words) {
      const dashboardRow = page.locator("main").getByText(release.replace(/\./g, " ").replace(/^Words /, "Words ")).first();
      await expect(dashboardRow.locator("xpath=ancestor::*[self::li or self::div][1]")).toContainText(word);
    }
  });

  test("a hand-off whose download has left the client opens the hand-off's own drawer, and Send again is held while it asks and shows the old failure again when it is refused", async ({ page }) => {
    await openScreen(page, "/queue");
    await seed(page, { releaseName: "Left.The.Client.2020.1080p-GRP", status: "failed", failureMessage: "Deluno could not submit the hand-off." });
    await openTransfers(page);

    await rowOf(page, "Left.The.Client.2020.1080p-GRP").click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Weir · failed")).toBeVisible();
    await expect(drawer.getByText("Deluno could not submit the hand-off.")).toBeVisible();
    // Nothing the download's own drawer holds is here: the client no longer has it.
    await expect(drawer.getByRole("button", { name: "Pause" })).toHaveCount(0);

    // Watch the drawer from the press to the answer, so the held moment is seen however quickly the server answers.
    await page.evaluate(() => {
      const w = window as unknown as { __held: boolean };
      w.__held = false;
      new MutationObserver(() => {
        if (document.querySelector("[role=dialog]")?.textContent?.includes("Sending to Weir again")) w.__held = true;
      }).observe(document.body, { subtree: true, childList: true, characterData: true });
    });
    const answered = page.waitForResponse((response) => response.url().includes("/retry") && response.request().method() === "POST");
    await drawer.getByRole("button", { name: "Send to Weir again" }).click();

    const reply = await answered;
    // No processor connection is set up on this install, so the server refuses, in words.
    expect(reply.status()).toBe(409);
    expect(((await reply.json()) as { message: string }).message).toContain("processor connection for this hand-off is unavailable");
    expect(await page.evaluate(() => (window as unknown as { __held: boolean }).__held), "the drawer read 'Sending to Weir again' while it asked").toBe(true);
    await expect(drawer.getByText("Deluno could not submit the hand-off.")).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Send to Weir again" })).toBeVisible();
  });

  test("a failed hand-off's Import original and Dismiss act on the real hand-off, and the original is not imported until the processor has stopped", async ({ page }) => {
    await openScreen(page, "/queue");
    const still = await seed(page, { releaseName: "Original.Still.2020.1080p-GRP", status: "submitted" });
    const failed = await seed(page, { releaseName: "Original.Failed.2020.1080p-GRP", status: "failed", failureMessage: "Weir could not read the file." });
    await openTransfers(page);

    // The processor may still deliver: the server refuses to import the original, in words.
    const refused = await api<{ message: string }>(page, "POST", `/api/integrations/processors/handoffs/${still.handoffId}/import-original`);
    expect(refused.status).toBe(409);
    expect(refused.body.message).toContain("Deluno will not import a file the processor may still deliver");

    await rowOf(page, "Original.Failed.2020.1080p-GRP").click();
    const answered = page.waitForResponse((response) => response.url().includes("/import-original") && response.request().method() === "POST");
    await page.getByRole("dialog").getByRole("button", { name: "Import original" }).click();
    const reply = await answered;
    expect(reply.status()).toBe(202);
    await expect(page.getByText("Original.Failed.2020.1080p-GRP will be imported unprocessed").first()).toBeVisible();
    const stored = (await api<Array<{ id: string; status: string }>>(page, "GET", "/api/integrations/processors/handoffs?take=50")).body.find((h) => h.id === failed.handoffId);
    expect(stored?.status).toBe("released");
  });

  test("a failure told by a dead-lettered import, its recovery case, its failed download and its hand-off is one row, and a second failure makes it two", async ({ page }) => {
    await openScreen(page, "/queue");
    await seed(page, {
      releaseName: "One.Failure.2022.1080p-GRP", status: "completed", outputPath: path.join(libraryFolder, "refined.mkv"), importJob: "dead-letter",
      dispatchImportFailure: "The file is not a video Deluno can read.", recoveryCase: true, failureMessage: "The import did not finish."
    });
    await openTransfers(page);

    const attention = page.locator("section, div").filter({ has: page.getByText("Needs Attention", { exact: true }) }).last();
    await expect(page.getByText("1 thing Deluno could not finish on its own")).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: "One.Failure.2022.1080p-GRP" })).toHaveCount(1);
    // Not listed again as the job, the case or the download.
    await expect(page.getByText("Import job", { exact: true })).toHaveCount(0);
    await expect(page.getByText("The file is not a video Deluno can read.")).toHaveCount(0);
    expect(await attention.count()).toBeGreaterThan(0);

    await seed(page, { releaseName: "Another.Failure.2022.1080p-GRP", status: "failed", failureMessage: "Weir refused the hand-off." });
    await openTransfers(page);
    await expect(page.getByText("2 things Deluno could not finish on its own")).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: "One.Failure.2022.1080p-GRP" })).toHaveCount(1);
  });

  test("Import & Naming lists only import jobs that are not already a problem row, with '<n> active · latest <m>' in its header, and a hand-off whose import is running is that import and not a second row", async ({ page }) => {
    await openScreen(page, "/queue");
    await seed(page, { releaseName: "Job.Queued.2021", withDispatch: false, importJob: "queued" });
    await seed(page, { releaseName: "Job.Running.2021", withDispatch: false, importJob: "running" });
    // A dead-lettered job is a problem row in Needs attention, not an import in progress.
    await seed(page, { releaseName: "Job.Dead.2021", withDispatch: false, importJob: "dead-letter" });
    // A hand-off whose output is being imported is that import, and stops being listed with the processor.
    await seed(page, { releaseName: "Job.OfAHandOff.2021.1080p-GRP", status: "completed", outputPath: path.join(libraryFolder, "x.mkv"), importJob: "running" });
    // A failed hand-off with an import behind it is one problem row: its import is not also in progress.
    await seed(page, { releaseName: "Job.OfAFailedHandOff.2021.1080p-GRP", status: "failed", failureMessage: "Weir refused the hand-off.", importJob: "running" });
    await openTransfers(page);

    await expect(page.getByText("4 active · latest 3")).toBeVisible();
    const card = page.locator("section, div").filter({ has: page.getByText("4 active · latest 3") }).last();
    await expect(card.getByRole("row")).toHaveCount(4); // the header and the three imports it lists
    await expect(card.getByText("running")).toHaveCount(2);
    await expect(card.getByText("queued")).toHaveCount(1);
    // The dead-lettered job is one problem row, and the failed hand-off another.
    await expect(page.getByText("2 things Deluno could not finish on its own")).toBeVisible();
    await expect(page.getByText("Import job", { exact: true })).toHaveCount(1);
    await expect(page.getByText("The destination file already exists.").first()).toBeVisible();
    await expect(page.getByRole("row").filter({ hasText: "Job.OfAFailedHandOff.2021.1080p-GRP" })).toHaveCount(1);
    // And the hand-off that is being imported is no longer a row of the processor's.
    await expect(page.getByRole("row").filter({ hasText: "Job.OfAHandOff.2021.1080p-GRP" })).toHaveCount(0);
  });
});
