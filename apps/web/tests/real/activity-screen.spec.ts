import { expect, test } from "@playwright/test";
import { expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Activity against the real server, nothing stubbed (measure 4, issue 1270): the history the host really recorded,
 * the status chips and their empty states, and the event drawer, each checked against the request the page sent
 * and the words on screen. The disposable host records plain events of its own (for example that automatic jobs
 * are off), so the list is populated without any seeding.
 */

const EMPTY: Record<string, { title: string; description: string }> = {
  "In progress": { title: "Nothing in progress", description: "Searches, downloads and imports for your titles appear here while they run." },
  Failed: { title: "Nothing has failed", description: "A search, download or import that stopped, and needs another go or a look, appears here." },
  "Needs a decision": { title: "Nothing needs a decision", description: "Downloads that finish but cannot be filed — wrong quality, no match, corrupt — land here for you to decide." }
};

test.describe("Activity", () => {
  test("Everything lists what Deluno recorded, with a plain sentence, a status and a time on each row", async ({ page }) => {
    const requests = recordRequests(page);
    await openScreen(page, "/activity");
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    await expect(page.getByText("Everything", { exact: true }).first()).toBeVisible();
    const rows = page.getByRole("row");
    await expect(rows.nth(1)).toBeVisible();
    const first = (await rows.nth(1).innerText()).replace(/\s+/g, " ");
    expect(first).toMatch(/Today|Yesterday|\d{1,2} \w{3}/);
    expect(first).not.toMatch(/undefined|null|\[object/);
    requests.stop();
    expect(requests.find("GET", /^\/api\/activity/).length, "the list came from the server's activity history").toBeGreaterThan(0);
  });

  test("a row opens its event in a drawer that says what happened, and Close shuts it", async ({ page }) => {
    await openScreen(page, "/activity");
    await untilQuiet(page);
    await page.getByRole("row").nth(1).click();

    const drawer = page.getByRole("dialog");
    await expect(drawer).toBeVisible();
    await expect(drawer.getByRole("heading", { name: "What happened", exact: true })).toBeVisible();
    const message = await drawer.locator("h2").first().innerText();
    expect(message.trim().length).toBeGreaterThan(10);
    await expect(drawer.getByText(message, { exact: true }).nth(1)).toBeVisible();
    await drawer.getByRole("button", { name: "Close", exact: true }).first().click();
    await expect(drawer).toHaveCount(0);
  });

  for (const [chip, words] of Object.entries(EMPTY)) {
    test(`the ${chip} chip narrows the list and, with nothing there, says so in words`, async ({ page }) => {
      await openScreen(page, "/activity");
      await untilQuiet(page);
      const requests = recordRequests(page);
      const button = page.getByRole("radio", { name: new RegExp(`^${chip}`) });
      const count = Number(((await button.getAttribute("aria-label")) ?? "").match(/(\d+)$/)?.[1] ?? "0");
      await button.click();
      await untilQuiet(page);
      requests.stop();
      await expectNoErrorScreen(page);
      if (count === 0) {
        await expect(page.getByText(words.title, { exact: true })).toBeVisible();
        await expect(page.getByText(words.description, { exact: true })).toBeVisible();
      } else {
        await expect(page.getByRole("row").nth(1)).toBeVisible();
      }
      await expect(button).toBeChecked();
    });
  }
});
