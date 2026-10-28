import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation > Keep Seeding against the real server, nothing stubbed (measure 4, issue 1270): the empty client and site cards; a client
 * made through the real route opens its drawer; Check it with no category says to enter one, and with one asks the real server, which
 * cannot reach a client nobody runs; the footer reads Unsaved changes and Cancel clears the check; the Tags card replaces a tag
 * that exists in any case, says where its torrents go, and shows the server's refusal of a name it will not keep.
 */

interface View { clients: Array<{ clientId: string; category: string | null }>; tagCategories: Array<{ tag: string; category: string }> }

const view = async (page: Page) => (await api<View>(page, "GET", "/api/download-health/keep-seeding")).body;
const open = async (page: Page) => { await openScreen(page, "/search-cycles/keep-seeding"); await untilQuiet(page); };
const toast = (page: Page) => page.locator("[data-sonner-toast]");

test.describe("Keep seeding", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);
  let clientId = "";

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/search-cycles/keep-seeding");
    await api(page, "PUT", "/api/download-health/keep-seeding", { clientCategories: {}, indexers: {}, tagCategories: {} });
    if (clientId) await api(page, "DELETE", `/api/download-clients/${clientId}`);
    await page.close();
  });

  test("with no download client and no torrent site both cards say so", async ({ page }) => {
    await open(page);
    await expect(page.getByText("No download clients", { exact: true })).toBeVisible();
    await expect(page.getByText("Add a download client first. A moved torrent stays in it, in the category you choose.", { exact: true })).toBeVisible();
    await expect(page.getByText("No torrent sites", { exact: true })).toBeVisible();
    await expect(page.getByText("Add a torrent site under Connections.", { exact: false })).toBeVisible();
  });

  test("Check it with no category says to enter one, and nothing is sent to the client", async ({ page }) => {
    await open(page);
    const made = await api<{ id: string }>(page, "POST", "/api/download-clients", {
      protocol: "qbittorrent", host: "127.0.0.1", port: 1, username: "u", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false
    });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    clientId = made.body.id;
    await page.reload();
    await untilQuiet(page);

    await page.getByRole("row").filter({ hasText: /qbittorrent/i }).first().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Keep-seeding category", { exact: true }).first()).toBeVisible();
    let checked = 0;
    page.on("request", (request) => { if (request.url().includes("/keep-seeding/check")) checked += 1; });
    await drawer.getByRole("button", { name: "Check it", exact: true }).click();
    await expect(drawer.getByText("Enter a category first.", { exact: true })).toBeVisible();
    expect(checked).toBe(0);
  });

  test("Check it with a category asks the server; a client nobody runs reads Could not check, and the footer, Cancel and the check result behave", async ({ page }) => {
    await open(page);
    await page.getByRole("row").filter({ hasText: /qbittorrent/i }).first().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Unsaved changes", { exact: true })).toHaveCount(0);
    await drawer.getByPlaceholder("For example keep-seeding").fill("keep-me");
    await expect(drawer.getByText("Unsaved changes", { exact: true })).toBeVisible();

    const answer = page.waitForResponse((response) => response.url().endsWith("/api/download-health/keep-seeding/check"));
    await drawer.getByRole("button", { name: "Check it", exact: true }).click();
    const body = await (await answer).json() as { status: string };
    expect(body.status).toBe("unreachable");
    await expect(drawer.getByText("Could not check", { exact: true })).toBeVisible();

    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    expect((await view(page)).clients.find((item) => item.clientId === clientId)?.category).toBeNull();

    await page.getByRole("row").filter({ hasText: /qbittorrent/i }).first().click();
    const again = page.getByRole("dialog");
    await expect(again.getByPlaceholder("For example keep-seeding")).toHaveValue("");
    await expect(again.getByText("Could not check", { exact: true })).toHaveCount(0);
    await expect(again.getByText("Unsaved changes", { exact: true })).toHaveCount(0);

    await again.getByPlaceholder("For example keep-seeding").fill("keep-me");
    await again.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect.poll(async () => (await view(page)).clients.find((item) => item.clientId === clientId)?.category).toBe("keep-me");
    await expect(page.getByRole("row").filter({ hasText: /qbittorrent/i }).first()).toContainText("keep-me");
  });

  test("adding a tag that already exists, in any case, replaces it and says where its torrents will move", async ({ page }) => {
    await open(page);
    await page.getByLabel("Tag name").fill("Ratio-Sensitive");
    await page.getByLabel("Category for this tag").fill("keep-a");
    await page.getByRole("button", { name: "Add tag", exact: true }).click();
    await expect(toast(page).filter({ hasText: "Torrents tagged Ratio-Sensitive will move to keep-a" })).toBeVisible();
    await expect.poll(async () => (await view(page)).tagCategories).toEqual([{ tag: "Ratio-Sensitive", category: "keep-a" }]);

    await page.getByLabel("Tag name").fill("ratio-sensitive");
    await page.getByLabel("Category for this tag").fill("keep-b");
    await page.getByRole("button", { name: "Add tag", exact: true }).click();
    await expect(toast(page).filter({ hasText: "Torrents tagged ratio-sensitive will move to keep-b" })).toBeVisible();
    await expect.poll(async () => (await view(page)).tagCategories).toEqual([{ tag: "ratio-sensitive", category: "keep-b" }]);
    await expect(page.getByRole("row").filter({ hasText: "ratio-sensitive" })).toHaveCount(1);
    await expect(page.getByRole("row").filter({ hasText: "keep-a" })).toHaveCount(0);
  });

  test("a tag name the server will not keep shows the server's own reason and keeps the form", async ({ page }) => {
    await open(page);
    await page.getByLabel("Tag name").fill("t".repeat(101));
    await page.getByLabel("Category for this tag").fill("keep-c");
    await page.getByRole("button", { name: "Add tag", exact: true }).click();
    await expect(toast(page).filter({ hasText: "A tag must be a plain name of up to 100 characters." })).toBeVisible();
    await expect(page.getByLabel("Tag name")).toHaveValue("t".repeat(101));
    expect((await view(page)).tagCategories.map((item) => item.tag)).toEqual(["ratio-sensitive"]);
  });
});
