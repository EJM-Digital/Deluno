import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Subtitle providers: the drawer's Priority and Enabled, against the real server, nothing stubbed (measure 4,
 * issue 1296). The provider is Subdl, which needs a key, so nothing here reaches the internet. Each step reads back through the
 * real API what the drawer saved.
 */
const KEY = "subdl";
const row = (page: Page) => page.getByRole("row").filter({ hasText: "Subdl" }).first();

interface Provider { key: string; configured: { priority: number; isEnabled: boolean; hasApiKey: boolean } | null }
const stored = async (page: Page) => (await api<Provider[]>(page, "GET", "/api/subtitle-providers")).body.find((p) => p.key === KEY)!;

test.describe("Subtitle provider priority", () => {
  test.setTimeout(90_000);

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/subtitle-providers");
    await api(page, "DELETE", `/api/subtitle-providers/${KEY}`);
    await page.close();
  });

  test("the drawer saves the priority and the Enabled switch, a blank priority becomes 100, and the list shows both", async ({ page }) => {
    await openScreen(page, "/indexers/subtitle-providers");
    await untilQuiet(page);
    await row(page).click();
    const drawer = page.getByRole("dialog", { name: "Subdl" });
    await expect(drawer.getByText("Lower is asked first. Deluno stops at the first provider that has the subtitle.")).toBeVisible();
    await expect(drawer.getByText("A provider that is off is never asked.")).toBeVisible();
    await drawer.getByLabel("API key").fill("example-key-123");
    await drawer.getByLabel("Priority").fill("3");
    await drawer.getByRole("switch").click();
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page.getByText("Subdl saved").first()).toBeVisible();
    expect((await stored(page)).configured).toMatchObject({ priority: 3, isEnabled: false });
    await expect(row(page)).toContainText("Priority 3");
    await expect(row(page).getByRole("switch", { name: "Enable Subdl" })).not.toBeChecked();

    await row(page).click();
    await expect(drawer.getByLabel("Priority")).toHaveValue("3");
    await drawer.getByLabel("Priority").fill("");
    await drawer.getByRole("switch").click();
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page.getByText("Subdl saved").first()).toBeVisible();
    expect((await stored(page)).configured).toMatchObject({ priority: 100, isEnabled: true });
    await expect(row(page)).toContainText("Priority 100");
    await expect(row(page).getByRole("switch", { name: "Pause Subdl" })).toBeChecked();
  });
});
