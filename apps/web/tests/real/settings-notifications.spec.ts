import http from "node:http";
import type { AddressInfo } from "node:net";
import { expect, test, type Locator, type Page } from "@playwright/test";
import { exerciseColumns } from "../helpers/columns";
import { api, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Notifications against the real server, nothing stubbed (measure 4, issue 1329). The webhooks are real records
 * made through the API; the address they post to is a small receiver this spec starts on 127.0.0.1, so the server really
 * posts and the spec reads what arrived. Every record is stamped and removed afterwards.
 */

interface Webhook { id: string; name: string; url: string; eventFilters: string; isEnabled: boolean; provider: string; tags: string | null; lastFiredUtc: string | null; lastError: string | null }
interface Delivery { id: string; webhookId: string; status: string; title: string; attemptCount: number }

const stamp = Date.now();
const hooks = async (page: Page) => (await api<Webhook[]>(page, "GET", "/api/notification-webhooks")).body;
const deliveries = async (page: Page, query = "") => (await api<Delivery[]>(page, "GET", `/api/notification-webhooks/deliveries?take=100${query}`)).body;

async function receiver(port = 0) {
  const received: string[] = [];
  const server = http.createServer((request, response) => {
    let body = "";
    request.on("data", (chunk) => { body += chunk; });
    request.on("end", () => { received.push(body); response.writeHead(200, { "Content-Type": "application/json" }); response.end("{}"); });
  });
  await new Promise<void>((resolve) => server.listen(port, "127.0.0.1", resolve));
  return { received, port: (server.address() as AddressInfo).port, close: () => new Promise<void>((resolve) => server.close(() => resolve())) };
}

async function makeHook(page: Page, name: string, url: string, extra: Record<string, unknown> = {}) {
  const made = await api<Webhook>(page, "POST", "/api/notification-webhooks", { name, url, eventFilters: "all", isEnabled: true, provider: "generic", configJson: null, tags: "", includeHealthWarnings: false, ...extra });
  expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
  return made.body;
}

let lounge = { id: "", port: 0 };
const loungeRow = (page: Page) => page.locator('#main-content [role="row"]').filter({ hasText: `127.0.0.1:${lounge.port}/` });
const rowOf = (page: Page, name: string): Locator => page.locator('#main-content [role="row"]').filter({ hasText: name });

test.describe("Settings > Notifications", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(60_000);
  let notificationsWere: boolean | null = null;

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/notifications");
    if (notificationsWere === null) notificationsWere = (await api<{ enableNotifications: boolean }>(page, "GET", "/api/settings")).body.enableNotifications;
    await untilQuiet(page);
  });

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/settings/notifications");
    for (const hook of await hooks(page)) if (hook.name.includes(String(stamp))) await api(page, "DELETE", `/api/notification-webhooks/${hook.id}`);
    if (notificationsWere !== null) await api(page, "PATCH" as "POST", "/api/settings", { enableNotifications: notificationsWere });
    await page.close();
  });

  test("with no webhooks the card says so, New webhook opens the drawer, and the Provider select offers the eight providers", async ({ page }) => {
    for (const hook of await hooks(page)) await api(page, "DELETE", `/api/notification-webhooks/${hook.id}`);
    await page.reload();
    await untilQuiet(page);
    await expect(page.getByText("No webhooks yet", { exact: true })).toBeVisible();
    await expect(page.getByText("Deluno can POST a JSON payload to any URL when it grabs, imports or upgrades a title, or when a health check fails.")).toBeVisible();

    await page.locator("#main-content").getByRole("button", { name: "New webhook", exact: true }).last().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("New webhook", { exact: true }).first()).toBeVisible();
    await expect(drawer.getByText("Deluno POSTs a JSON payload to this URL.")).toBeVisible();
    expect(await drawer.getByLabel("Provider").locator("option").allInnerTexts()).toEqual(["Generic webhook", "Discord", "Slack", "Telegram", "Pushover", "ntfy", "Gotify", "Apprise (generic URL)"]);
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });

  test("a webhook made in the drawer saves its Send when ticks, tags and Enabled switch, and its row and heading read them back", async ({ page }) => {
    const target = await receiver();
    try {
      await api(page, "POST", "/api/tags", { name: `lounge-${stamp}`, color: "teal" });
      await page.getByRole("button", { name: "New webhook", exact: true }).first().click();
      const drawer = page.getByRole("dialog");
      await drawer.getByLabel("URL", { exact: true }).fill(`http://127.0.0.1:${target.port}/hook`);
      await drawer.getByRole("checkbox", { name: /^Everything/ }).click();
      await drawer.getByRole("checkbox", { name: /^Test events only/ }).click();
      await drawer.getByRole("checkbox", { name: /^Grab/ }).click();
      await drawer.getByRole("checkbox", { name: /^Import/ }).click();
      await drawer.getByRole("checkbox", { name: /^Upgrade/ }).click();
      await drawer.getByRole("checkbox", { name: /^Health problems/ }).click();
      await drawer.getByRole("checkbox", { name: /^Leaving soon/ }).click();
      await drawer.getByRole("checkbox", { name: /^Deluno update/ }).click();
      await drawer.getByRole("textbox", { name: "Notification tags" }).fill(`lounge-${stamp}`);
      await drawer.getByRole("button", { name: `Add lounge-${stamp} to Notification tags` }).click();
      const requests = recordRequests(page);
      await drawer.getByRole("button", { name: "Add webhook", exact: true }).click();
      await expect(drawer.getByText("Webhook added", { exact: true })).toBeVisible();
      requests.stop();

      const posts = requests.find("POST", /^\/api\/notification-webhooks$/);
      expect(posts, JSON.stringify(requests.seen)).toHaveLength(1);
      const stored = (await hooks(page)).find((h) => h.tags === `lounge-${stamp}`)!;
      const filters = stored.eventFilters.split(",").sort();
      expect(filters.map((f) => f.trim())).toEqual(["app.update", "retention", "test"]);
      expect(stored.tags).toBe(`lounge-${stamp}`);
      expect(stored.isEnabled).toBe(true);
      lounge = { id: stored.id, port: target.port };
      await drawer.getByRole("button", { name: "Close", exact: true }).click();
      await page.reload();
      await untilQuiet(page);

      await expect(page.getByText(/^\d+ webhooks? · \d+ enabled · send Deluno events to other tools$/)).toBeVisible();
      const row = loungeRow(page);
      await expect(row).toContainText("Generic webhook");
      await expect(row).toContainText(`http://127.0.0.1:${target.port}/`);
      await expect(row).toContainText("Chosen events");
      await expect(row).toContainText("Never");
      await expect(row).toContainText("Paused");
    } finally {
      await target.close();
    }
  });

  test("closing a webhook drawer with an edit asks to discard, Discard closes, and the stored webhook stays as it was", async ({ page }) => {
    const hook = (await hooks(page)).find((h) => h.id === lounge.id)!;
    await loungeRow(page).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Provider").selectOption("discord");
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    const ask = page.getByRole("alertdialog").or(page.getByRole("dialog").last());
    await expect(ask.getByText("Discard unsaved changes?")).toBeVisible();
    await expect(ask.getByText("Your edits to this webhook haven't been saved.")).toBeVisible();
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    expect((await hooks(page)).find((h) => h.id === hook.id)?.provider).toBe("generic");
  });

  test("a webhook named through the API keeps its name when the drawer saves it, and a new address gives it a derived name again", async ({ page }) => {
    const name = `Movie night ${stamp}`;
    const hook = await makeHook(page, name, `http://127.0.0.1:1/keep-${stamp}`);
    expect(hook.name).toBe(name);
    await page.reload();
    await untilQuiet(page);
    await rowOf(page, name).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("switch", { name: /^Enabled/ }).click();
    await drawer.getByRole("button", { name: "Save webhook", exact: true }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    expect((await hooks(page)).find((h) => h.id === hook.id)?.name).toBe(name);
    await drawer.getByLabel("URL", { exact: true }).fill(`http://127.0.0.1:2/moved-${stamp}`);
    await drawer.getByRole("button", { name: "Save webhook", exact: true }).click();
    await expect(drawer.getByText("Saved just now")).toBeVisible();
    expect((await hooks(page)).find((h) => h.id === hook.id)?.name).not.toBe(name);
  });

  test("the drawer's Delivery section says never fired and warns while notifications are paused, and the Enabled switch pauses the webhook", async ({ page }) => {
    await api(page, "PATCH" as "POST", "/api/settings", { enableNotifications: false });
    await page.reload();
    await untilQuiet(page);
    await loungeRow(page).click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("never fired", { exact: true })).toBeVisible();
    await expect(drawer.getByText("Notifications are paused for the whole install, so this webhook will not fire.")).toBeVisible();
    await expect(drawer.getByText("Paused webhooks stay configured but never fire.")).toBeVisible();
    await drawer.getByRole("switch", { name: /^Enabled/ }).click();
    await drawer.getByRole("button", { name: "Save webhook", exact: true }).click();
    await expect(drawer.getByText("Saved just now", { exact: true })).toBeVisible();
    expect((await hooks(page)).find((h) => h.id === lounge.id)?.isEnabled).toBe(false);
    await drawer.getByRole("switch", { name: /^Enabled/ }).click();
    await drawer.getByRole("button", { name: "Save webhook", exact: true }).click();
    await expect(drawer.getByText("Saved just now", { exact: true })).toBeVisible();
    await drawer.getByRole("button", { name: "Close", exact: true }).click();
  });

  test("a test event reaches the receiver, the row shows when it fired, and Delivery history lists it", async ({ page }) => {
    await api(page, "PATCH" as "POST", "/api/settings", { enableNotifications: true });
    const target = await receiver();
    try {
      const hook = await makeHook(page, `Live ${stamp}`, `http://127.0.0.1:${target.port}/live`, { eventFilters: "test" });
      await page.reload();
      await untilQuiet(page);
      await rowOf(page, `127.0.0.1:${target.port}/`).click();
      const drawer = page.getByRole("dialog");
      await drawer.getByRole("button", { name: "Send a test event", exact: true }).click();
      await expect(page.getByText(`Test event sent to ${hook.name}`)).toBeVisible();
      await expect.poll(() => target.received.length).toBe(1);
      expect(JSON.parse(target.received[0])).toMatchObject({ title: "Deluno Webhook Test" });
      await expect(drawer.getByText(/^last fired /)).toBeVisible();
      await drawer.getByRole("button", { name: "Close", exact: true }).click();

      await expect(rowOf(page, `127.0.0.1:${target.port}/`)).toContainText("Delivering");
      await expect(page.getByText(/^\d+ recent delivery attempts · failures remain replayable$/)).toBeVisible();
      const delivered = (await deliveries(page, "&status=delivered")).filter((d) => d.webhookId === hook.id);
      expect(delivered).toHaveLength(1);
      const history = page.locator('#main-content [role="row"]').filter({ hasText: "Deluno Webhook Test" }).filter({ hasText: hook.name });
      await expect(history).toContainText("delivered");
      await expect(history).toContainText("1/");
      const bad = await api<{ errors: Record<string, string[]> }>(page, "GET", "/api/notification-webhooks/deliveries?status=lost");
      expect(bad.status).toBe(400);
      expect(bad.body.errors.status).toEqual(["Status must be pending, retrying, delivered, or dead-letter."]);
      const replayMissing = await api(page, "POST", "/api/notification-webhooks/deliveries/no-such-delivery/replay");
      expect(replayMissing.status).toBe(404);
    } finally {
      await target.close();
    }
  });

  test("a webhook whose address is down shows Failing and Last delivery failed, then Replay delivers it once the address is back", async ({ page }) => {
    await api(page, "PATCH" as "POST", "/api/settings", { enableNotifications: true });
    const probe = await receiver();
    const port = probe.port;
    await probe.close();
    const hook = await makeHook(page, `Down ${stamp}`, `http://127.0.0.1:${port}/down`, { eventFilters: "test" });
    await page.reload();
    await untilQuiet(page);
    await rowOf(page, `127.0.0.1:${port}/`).click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("button", { name: "Send a test event", exact: true }).click();
    await expect.poll(async () => (await hooks(page)).find((h) => h.id === hook.id)?.lastError ?? "", { timeout: 20_000 }).not.toBe("");
    await page.reload();
    await untilQuiet(page);
    const row = rowOf(page, `127.0.0.1:${port}/`);
    await expect(row).toContainText("Failing");
    await expect(row).toContainText("Last delivery failed");
    await row.click();
    const lastError = (await hooks(page)).find((h) => h.id === hook.id)!.lastError!;
    await expect(page.getByRole("dialog").getByText(lastError, { exact: false }).first()).toBeVisible();
    await page.getByRole("dialog").getByRole("button", { name: "Close", exact: true }).click();

    const failed = (await deliveries(page)).filter((d) => d.webhookId === hook.id);
    expect(failed.length).toBeGreaterThan(0);
    expect(["retrying", "dead-letter"]).toContain(failed[0].status);
    const filtered = await deliveries(page, `&status=${failed[0].status}`);
    expect(filtered.map((d) => d.id)).toContain(failed[0].id);
    expect(filtered.every((d) => d.status === failed[0].status)).toBe(true);
    const back = await receiver(port);
    try {
      await page.getByRole("button", { name: "Replay" }).first().click();
      await expect(page.getByText("Delivery replayed")).toBeVisible();
      await expect.poll(() => back.received.length).toBe(1);
      expect((await deliveries(page)).find((d) => d.id === failed[0].id)?.status).toBe("delivered");
    } finally {
      await back.close();
    }
  });

  test("the webhooks table sorts by a header, reorders by dragging, remembers both and resets", async ({ page }) => {
    for (const host of [`zulu-${stamp}`, `alpha-${stamp}`]) await makeHook(page, host, `http://${host}.example.test/x`);
    await page.reload();
    await untilQuiet(page);
    await exerciseColumns(page, {
      scope: () => page.locator("#main-content"),
      defaultOrder: ["name", "sendsTo", "events", "lastFired", "status", "on"],
      sortId: "sendsTo",
      cellsOf: async (scope) => {
        const urls = await scope.locator('[role="rowgroup"] [role="row"]').evaluateAll((rows) => rows.map((row) => (row.querySelectorAll('[role="cell"]')[1] as HTMLElement | undefined)?.innerText.split("\n")[0] ?? ""));
        return urls.filter((u) => u.includes(String(stamp)));
      },
      columnsButton: () => page.locator("#main-content").getByRole("button", { name: "Columns", exact: true }).first(),
      move: { from: "events", onto: "name" }
    });
  });
});
