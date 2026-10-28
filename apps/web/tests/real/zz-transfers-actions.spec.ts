import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * What Transfers offers for a hand-off that did not finish, and for several things at once, against the real server and the
 * real databases, nothing stubbed (measure 4, issue 1270). The starting records are written by the smoke host's seeding route
 * (TransfersSeedTestEndpoints), with the same repository calls the work planner makes; each button then goes through the real
 * routes. Every test clears the seeding it made, so the specs that follow meet the install as they found it.
 */

let libraryId = "";
let libraryFolder = "";

async function seed(page: Page, body: Record<string, unknown>) {
  const reply = await api<{ handoffId: string | null; dispatchId: string | null; importJobId: string | null }>(
    page, "POST", "/api/testing/transfers/handoff", { libraryId, ...body });
  expect(reply.status, JSON.stringify(reply.body)).toBe(200);
  return reply.body;
}

async function openTransfers(page: Page) {
  await page.goto("/queue");
  await untilQuiet(page);
  await expectNoErrorScreen(page);
}

const rowOf = (page: Page, release: string) => page.getByRole("row").filter({ hasText: release }).first();

test.describe("Transfers, acting on what did not finish", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/queue");
    libraryFolder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-transfer-actions-"));
    const created = await api<{ id: string }>(page, "POST", "/api/libraries", {
      name: "Transfer action films", mediaType: "movies", purpose: "collection", rootPath: libraryFolder, importWorkflow: "refine-before-import", processorName: "Weir"
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
    const cleared = await api(page, "POST", "/api/testing/transfers/clear");
    expect(cleared.status, JSON.stringify(cleared.body)).toBe(204);
  });

  test("a hand-off whose download was gone offers Search for another copy and nothing to send again", async ({ page }) => {
    await openScreen(page, "/queue");
    await seed(page, { releaseName: "Gone.Before.Clean.2021.1080p-GRP", status: "source-gone", failureMessage: "The download was gone." });
    await openTransfers(page);

    await rowOf(page, "Gone.Before.Clean.2021.1080p-GRP").click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("so there is nothing to send again or import")).toBeVisible();
    await expect(drawer.getByRole("button", { name: "Send to Weir again" })).toHaveCount(0);

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Search for another copy", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => r.method === "POST" && /search-again/.test(r.path)).length).toBe(1);
    requests.stop();
    await expectNoErrorScreen(page);
  });

  test("a failed hand-off offers Search for another release, Keep waiting and Dismiss, each acting on that hand-off alone", async ({ page }) => {
    await openScreen(page, "/queue");
    const names = { search: "Offers.Search.2021.1080p-GRP", wait: "Offers.Wait.2021.1080p-GRP", dismiss: "Offers.Dismiss.2021.1080p-GRP" } as const;
    const made: Record<string, string> = {};
    for (const [key, releaseName] of Object.entries(names)) made[key] = (await seed(page, { releaseName, status: "failed", failureMessage: "Weir could not read the file." })).handoffId!;
    await openTransfers(page);
    const requests = recordRequests(page);
    const posted = (pattern: RegExp, id: string) => requests.seen.filter((r) => r.method === "POST" && pattern.test(r.path) && r.path.includes(id)).length;

    await rowOf(page, names.search).click();
    await page.getByRole("dialog").getByRole("button", { name: "Search for another release", exact: true }).click();
    await expect.poll(() => posted(/search-again/, made.search)).toBe(1);
    await untilQuiet(page);
    await page.keyboard.press("Escape");

    await rowOf(page, names.wait).click();
    await page.getByRole("dialog").getByRole("button", { name: "Keep waiting", exact: true }).click();
    await expect.poll(() => posted(/keep-waiting/, made.wait)).toBe(1);
    await untilQuiet(page);
    await page.keyboard.press("Escape");

    await rowOf(page, names.dismiss).click();
    await page.getByRole("dialog").getByRole("button", { name: "Dismiss this hand-off", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => /dismiss/.test(r.path) && r.path.includes(made.dismiss)).length).toBe(1);
    requests.stop();
    await expect(page.getByText(`${names.dismiss} dismissed`).first()).toBeVisible();
    expect(requests.seen.some((r) => r.path.includes(made.search) && /keep-waiting|dismiss/.test(r.path)), "each button touched only its own hand-off").toBe(false);
  });

  test("Select all to dismiss, one row's own switch and Clear dismiss selection choose what Dismiss clears, and Dismiss clears only those", async ({ page }) => {
    await openScreen(page, "/queue");
    const names = ["Bulk.One.2021.1080p-GRP", "Bulk.Two.2021.1080p-GRP", "Bulk.Three.2021.1080p-GRP"];
    for (const releaseName of names) await seed(page, { releaseName, status: "failed", failureMessage: "Weir could not read the file." });
    await openTransfers(page);

    await page.getByRole("button", { name: /^Select all \d+ to dismiss$/ }).click();
    await expect(page.getByRole("button", { name: /^Dismiss \d+ selected$/ })).toBeVisible();
    await page.getByRole("button", { name: "Clear dismiss selection", exact: true }).click();
    await expect(page.getByRole("button", { name: /^Select all \d+ to dismiss$/ })).toBeVisible();

    await page.getByRole("switch", { name: "Select Bulk.Two.2021.1080p-GRP" }).click();
    await expect(page.getByRole("button", { name: "Dismiss 1 selected", exact: true })).toBeVisible();
    await page.getByRole("switch", { name: "Select Bulk.Two.2021.1080p-GRP" }).click();
    await expect(page.getByRole("button", { name: /^Select all \d+ to dismiss$/ })).toBeVisible();

    await page.getByRole("switch", { name: "Select Bulk.One.2021.1080p-GRP" }).click();
    const requests = recordRequests(page);
    await page.getByRole("button", { name: "Dismiss 1 selected", exact: true }).click();
    await expect.poll(() => requests.seen.filter((r) => /dismiss/.test(r.path)).length).toBe(1);
    requests.stop();
    // What happened stays on record, but it no longer needs the person: only the two not chosen are still offered for dismissing.
    await expect(page.getByRole("switch", { name: "Select Bulk.One.2021.1080p-GRP" })).toHaveCount(0);
    await expect(page.getByRole("switch", { name: "Select Bulk.Two.2021.1080p-GRP" })).toBeVisible();
    await expect(page.getByRole("switch", { name: "Select Bulk.Three.2021.1080p-GRP" })).toBeVisible();
  });

  test("a failed import job offers Retry failed, which asks the server once and says what it did", async ({ page }) => {
    await openScreen(page, "/queue");
    await seed(page, { releaseName: "Import.Gave.Up.2021.1080p-GRP", status: "released", importJob: "dead-letter" });
    await openTransfers(page);

    const retry = page.getByRole("button", { name: /^Retry \d+ failed$/ });
    await expect(retry).toBeVisible();
    const requests = recordRequests(page);
    await retry.click();
    await expect.poll(() => requests.seen.filter((r) => r.method === "POST" && /retry|requeue/i.test(r.path)).length).toBeGreaterThan(0);
    requests.stop();
    await expectNoErrorScreen(page);
  });
});
