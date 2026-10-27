import { expect, test } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * System > Health, the Providers list and a provider's drawer, against the real server, nothing stubbed (measure 4, issue 1270):
 * a download client that was never tested is a row; clicking it opens a drawer that says in words that no result is recorded yet,
 * and Close shuts it.
 */

let clientId = "";

test.describe("System > Health, a provider's drawer", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/system");
    const made = await api<{ id: string }>(page, "POST", "/api/download-clients", {
      protocol: "qbittorrent", host: "127.0.0.1", port: 18099, username: "u", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false
    });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    clientId = made.body.id;
    await page.close();
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/system");
    await api(page, "DELETE", `/api/download-clients/${clientId}`);
    await page.close();
  });

  test("a provider row opens its drawer with the health result in words, and Close shuts it", async ({ page }) => {
    await openScreen(page, "/system");
    await untilQuiet(page);
    await expectNoErrorScreen(page);
    const row = page.getByRole("row").filter({ hasText: "Download client" }).filter({ hasText: "qbittorrent" }).first();
    await expect(row).toBeVisible({ timeout: 45_000 });
    await row.click();
    const drawer = page.getByRole("dialog").first();
    await expect(drawer.getByText("Health result")).toBeVisible();
    await expect(drawer.getByText("Last checked")).toBeVisible();
    await expect(drawer.getByText("What happens next")).toBeVisible();
    await expect(drawer.getByText("Open the provider connection to run a new test or update its settings.")).toBeVisible();
    await drawer.getByRole("button", { name: "Close", exact: true }).first().click();
    await expect(drawer).toHaveCount(0);
  });
});
