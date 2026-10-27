import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Changing the tags on several download clients at once, against the real server, nothing stubbed (measure 4, issue 1270): the
 * Select switch, a row's own tick, the Tags dialog with its three ways to apply, Cancel and Clear selection change nothing, and
 * the apply button writes the tag to the ticked clients only.
 */

const stamp = `bt${Date.now()}`;
const ids: string[] = [];
const ports = [18081, 18082];
let tagName = "";

interface Client { id: string; name: string; tags?: string | string[] | null }

async function clients(page: Page) {
  return (await api<Client[]>(page, "GET", "/api/download-clients")).body.filter((c) => ids.includes(c.id));
}

const tagsOf = (client: Client) => (Array.isArray(client.tags) ? client.tags.join(",") : client.tags ?? "");

test.describe("Download clients, tags on several at once", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers");
    for (const port of ports) {
      const made = await api<{ id: string }>(page, "POST", "/api/download-clients", {
        protocol: "qbittorrent", host: "127.0.0.1", port, username: "u", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false
      });
      expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
      ids.push(made.body.id);
    }
    const tag = `bulkt${stamp}`;
    const t = await api<{ id: string }>(page, "POST", "/api/tags", { name: tag, color: "#3b82f6" });
    expect(t.status, JSON.stringify(t.body)).toBeLessThan(300);
    tagName = tag;
    tagId = t.body.id;
    await page.close();
  });
  let tagId = "";

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers");
    for (const id of ids) await api(page, "DELETE", `/api/download-clients/${id}`);
    await api(page, "DELETE", `/api/tags/${tagId}`);
    await page.close();
  });

  test("ticking clients shows the bar; Cancel and Clear selection change nothing; Add tags writes the tag to the ticked clients only", async ({ page }) => {
    await openScreen(page, "/indexers");
    await page.goto("/indexers/download-clients");
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    await page.getByRole("button", { name: "Select", exact: true }).click();
    await page.getByRole("checkbox", { name: /^Select qBittorrent/ }).first().check();
    const bar = page.getByRole("toolbar", { name: "Bulk download clients actions" });
    await expect(bar.getByText("1 selected")).toBeVisible();

    await bar.getByRole("button", { name: "Change tags on the selected download clients" }).click();
    const dialog = page.getByRole("dialog", { name: /Change tags: 1 selected download client/ });
    await expect(dialog).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    expect((await clients(page)).every((c) => !tagsOf(c).includes(tagName))).toBe(true);

    await bar.getByRole("button", { name: "Clear selection", exact: true }).click();
    await expect(page.getByRole("toolbar", { name: "Bulk download clients actions" })).toHaveCount(0);

    await page.getByRole("checkbox", { name: /^Select qBittorrent/ }).first().check();
    await page.getByRole("button", { name: "Change tags on the selected download clients" }).click();
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Add tags", exact: true })).toBeDisabled();
    await dialog.getByRole("textbox", { name: "Tags to apply" }).fill(tagName);
    await dialog.getByRole("button", { name: `Add ${tagName} to Tags to apply` }).click();
    const requests = recordRequests(page);
    await dialog.getByRole("button", { name: "Add tags", exact: true }).click();
    await expect(dialog).toHaveCount(0);
    requests.stop();
    expect(requests.seen.filter((r) => r.method !== "GET" && /tags/.test(r.path))).toHaveLength(1);
    await expect.poll(async () => (await clients(page)).filter((c) => tagsOf(c).includes(tagName)).map((c) => c.id)).toHaveLength(1);
  });
});
