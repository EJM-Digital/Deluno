import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Download clients, the client drawer, against the real server (measure 4, issue 1296). The client is a disabled
 * SABnzbd address nobody listens on; nothing reaches another app. Each test reads back through the real API what the drawer saved.
 */

const stamp = `cd${Date.now()}`;
// Set while proving each test can fail: the expected outcome is flipped, and every test below must then go red.
const BREAK = Boolean(process.env.DELUNO_SPEC_BREAK);
const host = `client-${stamp}.example.test`;

interface Client { id: string; host: string; useSsl?: boolean; urlBase?: string | null; name?: string }
const clients = async (page: Page) => (await api<Client[]>(page, "GET", "/api/download-clients")).body;
const mine = async (page: Page) => (await clients(page)).find((item) => item.host === host);
const open = async (page: Page) => { await openScreen(page, "/indexers/download-clients"); await untilQuiet(page); };

test.describe("Download client drawer", () => {
  test.setTimeout(90_000);

  test.beforeEach(async ({ page }) => {
    await open(page);
    const made = await api(page, "POST", "/api/download-clients", { protocol: "sabnzbd", host, port: 8080, username: "u", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    await page.reload();
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    const left = await mine(page);
    if (left) await api(page, "DELETE", `/api/download-clients/${left.id}`);
  });

  test("Use SSL and the URL base are saved with the client, and the URL base is kept without its leading and trailing slash", async ({ page }) => {
    await page.getByRole("row").filter({ hasText: host }).first().click();
    const drawer = page.getByRole("dialog");
    const ssl = drawer.getByRole("switch", { name: "Use SSL" }).first();
    await expect(ssl).not.toBeChecked();
    await ssl.click();
    await drawer.getByRole("button", { name: /^Advanced/ }).first().click();
    await drawer.getByLabel("URL base").fill("/sabnzbd/");
    await drawer.getByRole("button", { name: "Save client" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    const saved = await mine(page);
    expect(saved?.useSsl).toBe(!BREAK);
    expect(saved?.urlBase).toBe("sabnzbd");
  });

  test("closing a drawer with unsaved edits asks first: Cancel keeps editing, Discard drops them without a save", async ({ page }) => {
    await page.getByRole("row").filter({ hasText: host }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("switch", { name: "Use SSL" }).first().click();
    const requests = recordRequests(page);
    await page.keyboard.press("Escape");
    const ask = page.getByRole("dialog", { name: "Discard unsaved changes?" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(ask).toHaveCount(0);
    await expect(drawer.getByRole("switch", { name: "Use SSL" }).first()).toBeChecked();
    await page.keyboard.press("Escape");
    await ask.getByRole("button", { name: BREAK ? "Keep" : "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    requests.stop();
    expect(requests.seen.filter((r) => r.method !== "GET")).toEqual([]);
    expect((await mine(page))?.useSsl).toBeFalsy();
  });
});
