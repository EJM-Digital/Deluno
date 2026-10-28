import { expect, test, type Page } from "@playwright/test";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Proxies against the real server (measure 4, issue 1296). The proxies point at addresses nobody listens on and the
 * indexer using one is switched off; nothing reaches another app. Each test reads back through the real API what the screen showed or saved.
 */

const stamp = `px${Date.now()}`;
const httpAddress = `http://${stamp}-http.example.test:3128`;
const helperAddress = `http://${stamp}-helper.example.test:8191`;

interface Proxy { id: string; name: string; address: string; proxyType: string; isEnabled: boolean; username?: string | null; assignedIndexerNames: string[]; hasPassword?: boolean; passwordConfigured?: boolean }
const proxies = async (page: Page) => (await api<Proxy[]>(page, "GET", "/api/indexer-proxies")).body;
const byAddress = async (page: Page, address: string) => (await proxies(page)).find((item) => item.address === address);
const open = async (page: Page) => { await openScreen(page, "/indexers/proxies"); await untilQuiet(page); };
const row = (page: Page, address: string) => page.getByRole("row").filter({ hasText: address }).first();

test.describe("Proxies", () => {
  test.setTimeout(90_000);
  let indexerId = "";

  test.afterEach(async ({ page }) => {
    for (const item of await proxies(page)) if (item.address.includes(stamp)) await api(page, "DELETE", `/api/indexer-proxies/${item.id}`);
    if (indexerId) await api(page, "DELETE", `/api/indexers/${indexerId}`);
    indexerId = "";
  });

  test("the list counts the proxies and shows each one's address, type, the indexers using it and whether it is on", async ({ page }) => {
    await open(page);
    const made = await api<Proxy>(page, "POST", "/api/indexer-proxies", { proxyType: "http", address: httpAddress, username: "u", password: "example-pw", isEnabled: true });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    const indexer = await api<{ id: string }>(page, "POST", "/api/indexers", { name: `Uses proxy ${stamp}`, protocol: "torznab", baseUrl: `http://uses-${stamp}.example.test`, apiPath: "/api", apiKey: "example-key", priority: 25, categories: "2000", isEnabled: false, proxyId: made.body.id });
    expect(indexer.status, JSON.stringify(indexer.body)).toBeLessThan(300);
    indexerId = indexer.body.id;
    await page.reload();
    await untilQuiet(page);

    const total = (await proxies(page)).length;
    await expect(page.getByText(`${total} ${total === 1 ? "proxy" : "proxies"}`, { exact: true })).toBeVisible();
    for (const column of ["Name", "Type", "Used by", "On"]) await expect(page.getByRole("columnheader", { name: column })).toBeVisible();
    const line = row(page, httpAddress);
    await expect(line).toContainText("HTTP");
    await expect(line).toContainText("1 indexer");
    await expect(line).toContainText("On");
  });

  test("the Used by section of the drawer names the indexers assigned to the proxy", async ({ page }) => {
    await open(page);
    const made = await api<Proxy>(page, "POST", "/api/indexer-proxies", { proxyType: "http", address: httpAddress, isEnabled: true });
    const idle = await api<Proxy>(page, "POST", "/api/indexer-proxies", { proxyType: "socks5", address: `socks5://${stamp}-idle.example.test:1080`, isEnabled: true });
    expect(idle.status).toBeLessThan(300);
    const indexer = await api<{ id: string }>(page, "POST", "/api/indexers", { name: `Uses proxy ${stamp}`, protocol: "torznab", baseUrl: `http://uses-${stamp}.example.test`, apiPath: "/api", apiKey: "example-key", priority: 25, categories: "2000", isEnabled: false, proxyId: made.body.id });
    indexerId = indexer.body.id;
    await page.reload();
    await untilQuiet(page);

    await row(page, httpAddress).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText(`1 indexer: Uses proxy ${stamp}`)).toBeVisible();
    await page.keyboard.press("Escape");
    await row(page, `${stamp}-idle.example.test`).click();
    await expect(page.getByRole("dialog").getByText("No indexer is assigned to this proxy yet.")).toBeVisible();
  });

  test("a FlareSolverr proxy has no username or password, and a blank password on edit keeps the saved one", async ({ page }) => {
    await open(page);
    const secured = await api<Proxy>(page, "POST", "/api/indexer-proxies", { proxyType: "http", address: httpAddress, username: "u", password: "example-pw", isEnabled: true });
    const helper = await api<Proxy>(page, "POST", "/api/indexer-proxies", { proxyType: "flaresolverr", address: helperAddress, isEnabled: true });
    expect(secured.status + helper.status).toBeLessThan(600);
    await page.reload();
    await untilQuiet(page);

    await row(page, helperAddress).click();
    let drawer = page.getByRole("dialog");
    await expect(drawer.getByLabel("Username")).toHaveCount(0);
    await expect(drawer.getByLabel("Password")).toHaveCount(0);
    await page.keyboard.press("Escape");

    await row(page, httpAddress).click();
    drawer = page.getByRole("dialog");
    await expect(drawer.getByLabel("Password")).toHaveValue("");
    await expect(drawer.getByLabel("Password")).toHaveAttribute("placeholder", /unchanged/);
    await drawer.getByLabel("Username").fill("renamed");
    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Save proxy" }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    requests.stop();
    const put = requests.find("PUT", /\/api\/indexer-proxies\//)[0];
    expect(put, "the save was sent").toBeTruthy();
    expect(Boolean((put.body as { password?: string }).password)).toBe(false);
    expect((await byAddress(page, httpAddress))?.username).toBe("renamed");
    const unreadable = (await byAddress(page, httpAddress)) as (Proxy & { secretUnreadable?: boolean }) | undefined;
    expect(unreadable?.secretUnreadable ?? false).toBe(false);
  });

  test("the list follows a proxy added or removed elsewhere with no reload", async ({ page }) => {
    await open(page);
    await expect(row(page, httpAddress)).toHaveCount(0);
    const made = await api<Proxy>(page, "POST", "/api/indexer-proxies", { proxyType: "http", address: httpAddress, isEnabled: true });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    await expect(row(page, httpAddress)).toBeVisible();
    expect((await api(page, "DELETE", `/api/indexer-proxies/${made.body.id}`)).status).toBeLessThan(300);
    await expect(row(page, httpAddress)).toHaveCount(0);
  });
});
