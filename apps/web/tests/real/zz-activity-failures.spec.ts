import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * What Activity offers for something that failed, against the real server and the real databases, nothing stubbed (measure 4,
 * issue 1270). The starting records are written by the smoke host's seeding route (TransfersSeedTestEndpoints) with the same
 * repository calls the work planner makes; every control then goes through the real routes. Each test clears the seeding it made.
 */

let libraryId = "";
let libraryFolder = "";

async function seed(page: Page, body: Record<string, unknown>) {
  const reply = await api<{ handoffId: string | null; importJobId: string | null }>(page, "POST", "/api/testing/transfers/handoff", { libraryId, ...body });
  expect(reply.status, JSON.stringify(reply.body)).toBe(200);
  return reply.body;
}

async function openFailed(page: Page) {
  await page.goto("/activity");
  await untilQuiet(page);
  await page.getByRole("radio", { name: /^Failed/ }).click();
  await untilQuiet(page);
  await expectNoErrorScreen(page);
}

test.describe("Activity, acting on what failed", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/activity");
    libraryFolder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-activity-failures-"));
    const created = await api<{ id: string }>(page, "POST", "/api/libraries", {
      name: "Activity failure films", mediaType: "movies", purpose: "collection", rootPath: libraryFolder, importWorkflow: "refine-before-import", processorName: "Weir"
    });
    expect(created.status, JSON.stringify(created.body)).toBeLessThan(300);
    libraryId = created.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/activity");
    await api(page, "POST", "/api/testing/transfers/clear");
    await api(page, "DELETE", `/api/libraries/${libraryId}`);
    await page.close();
    fs.rmSync(libraryFolder, { recursive: true, force: true });
  });

  test.afterEach(async ({ page }) => {
    const cleared = await api(page, "POST", "/api/testing/transfers/clear");
    expect(cleared.status, JSON.stringify(cleared.body)).toBe(204);
  });

  test("a filter that matches nothing says so, and Clear filters brings the history back", async ({ page }) => {
    await openScreen(page, "/activity?file=" + encodeURIComponent(`nothing-${Date.now()}.mkv`));
    await untilQuiet(page);
    await expect(page.getByText("Nothing matches these filters", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Clear filters", exact: true }).last().click();
    await expect(page.getByText("Nothing matches these filters", { exact: true })).toHaveCount(0);
    await expect(page.getByRole("row").nth(1)).toBeVisible();
  });

  test("a failed import job opens its drawer; Try again asks the server to run it again and Dismiss this job stops it counting as failed", async ({ page }) => {
    await openScreen(page, "/activity");
    await seed(page, { releaseName: "Activity.Job.Failed.2021.1080p-GRP", status: "released", importJob: "dead-letter" });
    await openFailed(page);

    const retry = page.getByRole("button", { name: "Retry failed jobs", exact: true });
    await expect(retry).toBeVisible();

    const row = page.getByRole("row").filter({ hasText: /import/i }).first();
    await row.click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Put it right")).toBeVisible();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Try again", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => r.method === "POST" && /retry|requeue/i.test(r.path)).length).toBeGreaterThan(0);
    await untilQuiet(page);
    requests.stop();
    await expectNoErrorScreen(page);
  });

  test("Dismiss this job on a failed import job sends one dismissal", async ({ page }) => {
    await openScreen(page, "/activity");
    await seed(page, { releaseName: "Activity.Job.Dismiss.2021.1080p-GRP", status: "released", importJob: "dead-letter" });
    await openFailed(page);
    await page.getByRole("row").filter({ hasText: /import/i }).first().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Dismiss this job")).toBeVisible();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Dismiss", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => /dismiss/i.test(r.path) || (r.method === "DELETE" && /jobs/.test(r.path))).length).toBe(1);
    requests.stop();
    await expectNoErrorScreen(page);
  });

  test("the drawer's fix link goes to the screen where the failure is put right", async ({ page }) => {
    await openScreen(page, "/activity");
    await seed(page, { releaseName: "Activity.Job.Link.2021.1080p-GRP", status: "released", importJob: "dead-letter" });
    await openFailed(page);
    await page.getByRole("row").filter({ hasText: /import/i }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.locator("a[href='/queue']").first().click();
    await expect(page).toHaveURL(/\/queue/);
    await expectNoErrorScreen(page);
  });
});
