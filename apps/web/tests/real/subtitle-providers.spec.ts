import { expect, test } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Subtitle providers against the real server, nothing stubbed (measure 4, issue 1270): Test says in words
 * why it cannot run without an account, Save keeps the key and priority on the server (never sending the key back), and the
 * On switch pauses and re-enables the provider. The provider used is Subdl, which needs a key, so nothing here reaches the internet.
 */
const KEY = "subdl";
const row = (page: import("@playwright/test").Page) => page.getByRole("row").filter({ hasText: "Subdl" }).first();

interface Provider { key: string; configured: { priority: number; isEnabled: boolean; hasApiKey: boolean } | null }
const stored = async (page: import("@playwright/test").Page) => (await api<Provider[]>(page, "GET", "/api/subtitle-providers")).body.find((p) => p.key === KEY)!;

test.describe("Subtitle providers", () => {
  test.describe.configure({ mode: "serial" });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/subtitle-providers");
    await api(page, "DELETE", `/api/subtitle-providers/${KEY}`);
    await page.close();
  });

  test("a provider that needs a key cannot be switched on before it is set up, and Test says why it cannot run", async ({ page }) => {
    await openScreen(page, "/indexers/subtitle-providers");
    await api(page, "DELETE", `/api/subtitle-providers/${KEY}`);
    await page.reload();
    await untilQuiet(page);
    await expect(row(page).getByRole("switch", { name: "Enable Subdl" })).toBeDisabled();
    await expect(row(page)).toContainText("Not set up");

    await row(page).click();
    const drawer = page.getByRole("dialog", { name: "Subdl" });
    await expect(drawer.getByText("Not set up yet.")).toBeVisible();
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Test", exact: true }).click();
    await expect(page.getByText("Subdl needs its account details before it can be tested.").first()).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/subtitle-providers\/subdl\/test$/)).toHaveLength(1);
    expect(requests.seen.filter((r) => r.method === "PUT"), "testing saves nothing").toEqual([]);
    expect((await stored(page)).configured).toBeNull();
  });

  test("Save keeps the key and priority on the server, the list says a key is saved, and the drawer never shows it back", async ({ page }) => {
    await openScreen(page, "/indexers/subtitle-providers");
    await untilQuiet(page);
    await row(page).click();
    const drawer = page.getByRole("dialog", { name: "Subdl" });
    await drawer.getByLabel("API key").fill("example-key-123");
    await drawer.getByLabel("Priority").fill("7");
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page.getByText("Subdl saved").first()).toBeVisible();
    requests.stop();
    const puts = requests.find("PUT", /^\/api\/subtitle-providers\/subdl$/);
    expect(puts).toHaveLength(1);
    expect(puts[0].body).toMatchObject({ providerKey: "subdl", apiKey: "example-key-123", priority: 7, isEnabled: true });

    const now = await stored(page);
    expect(now.configured).toMatchObject({ priority: 7, isEnabled: true, hasApiKey: true });
    expect(JSON.stringify(now)).not.toContain("example-key-123");
    await expect(drawer).toHaveCount(0);
    await expect(row(page)).toContainText("Saved: key");
    await expect(row(page)).toContainText("Priority 7");

    await row(page).click();
    await expect(drawer.getByText("A key is saved. Leave this blank to keep it.")).toBeVisible();
    await expect(drawer.getByLabel("API key")).toHaveValue("");
  });

  test("Saving with the key left blank keeps the saved key, and the On switch pauses and re-enables the provider", async ({ page }) => {
    await openScreen(page, "/indexers/subtitle-providers");
    await untilQuiet(page);
    await row(page).click();
    const drawer = page.getByRole("dialog", { name: "Subdl" });
    await drawer.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page.getByText("Subdl saved").first()).toBeVisible();
    expect((await stored(page)).configured?.hasApiKey).toBe(true);

    const requests = recordRequests(page);
    await row(page).getByRole("switch", { name: "Pause Subdl" }).click();
    await expect.poll(async () => (await stored(page)).configured?.isEnabled).toBe(false);
    await expect(row(page)).toContainText("Off");
    await row(page).getByRole("switch", { name: "Enable Subdl" }).click();
    await expect.poll(async () => (await stored(page)).configured?.isEnabled).toBe(true);
    requests.stop();
    expect(requests.find("PUT", /^\/api\/subtitle-providers\/subdl$/)).toHaveLength(2);
    expect((await stored(page)).configured?.hasApiKey, "toggling keeps the key").toBe(true);
  });

  test("Escape closes the drawer without saving anything", async ({ page }) => {
    await openScreen(page, "/indexers/subtitle-providers");
    await untilQuiet(page);
    const requests = recordRequests(page);
    await row(page).click();
    const drawer = page.getByRole("dialog", { name: "Subdl" });
    await expect(drawer).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);
    requests.stop();
    expect(requests.seen.filter((r) => r.method !== "GET")).toEqual([]);
  });
});
