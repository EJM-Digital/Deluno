import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Download clients: the list's heading, Categories and Now cells, Select mode, and the drawer's host and port
 * presets with "Use it for libraries with no client yet", against the real server (measure 4, issue 1296). Every client is a
 * disabled SABnzbd address nobody listens on, so nothing reaches another app. Each test reads back through the real API.
 */

const stamp = `dcl${Date.now()}`;
const hostOf = (name: string) => `${name}-${stamp}.example.test`;

interface Client { id: string; host: string; port: number; moviesCategory?: string; tvCategory?: string; name?: string; lastHealthTestUtc?: string | null }
const clients = async (page: Page) => (await api<Client[]>(page, "GET", "/api/download-clients")).body;
const clientAt = async (page: Page, host: string) => (await clients(page)).find((item) => item.host === host);
const seed = async (page: Page, name: string, extra: Record<string, unknown> = {}) => {
  const made = await api<Client>(page, "POST", "/api/download-clients", { protocol: "sabnzbd", host: hostOf(name), port: 8080, username: "u", password: "example-pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false, ...extra });
  expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
  return made.body;
};
const row = (page: Page, name: string) => page.getByRole("row").filter({ hasText: hostOf(name) }).first();
const reload = async (page: Page) => { await page.reload(); await untilQuiet(page); };

test.describe("Download clients list and drawer", () => {
  test.setTimeout(90_000);
  let folder = "";
  let libraryId = "";

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/indexers/download-clients");
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => {
    for (const item of await clients(page)) if (item.host.endsWith(`-${stamp}.example.test`) || (item.host === "localhost" && item.name?.includes(stamp))) await api(page, "DELETE", `/api/download-clients/${item.id}`);
    if (libraryId) await api(page, "DELETE", `/api/libraries/${libraryId}`);
    libraryId = "";
    if (folder) fs.rmSync(folder, { recursive: true, force: true });
    folder = "";
  });

  test("the list heading counts the clients, the Categories cell shows both categories or warns that files will mix, and a client never tested says so", async ({ page }) => {
    await seed(page, "apart");
    await seed(page, "same", { moviesCategory: "shared", tvCategory: "shared" });
    const linked = await seed(page, "linked");
    const mapping = await api(page, "POST", `/api/download-clients/${linked.id}/path-mappings`, { remotePath: "/remote/downloads", localPath: os.tmpdir() });
    expect(mapping.status, JSON.stringify(mapping.body)).toBeLessThan(300);
    await reload(page);
    await expect(row(page, "linked")).toContainText("1 file-location link");
    const total = (await clients(page)).length;
    await expect(page.getByText(`${total} ${total === 1 ? "client" : "clients"}`, { exact: true })).toBeVisible();
    await expect(row(page, "apart")).toContainText("movies · tv");
    await expect(row(page, "apart")).toContainText("Not tested");
    await expect(row(page, "same")).toContainText("shared · shared");
    await expect(row(page, "same")).toContainText("Same for both — files will mix");
  });

  test("testing a client turns its Now cell from Not tested to Tested, with the age", async ({ page }) => {
    await seed(page, "tested");
    await reload(page);
    await row(page, "tested").click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("button", { name: "Test", exact: true }).click();
    await expect.poll(async () => (await clientAt(page, hostOf("tested")))?.lastHealthTestUtc ?? null).not.toBeNull();
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);
    await expect(row(page, "tested")).toContainText(/Tested .*ago|Tested just now|Tested now/);
    await expect(row(page, "tested")).not.toContainText("Not tested");
  });

  test("Select turns on a checkbox column with a header checkbox, a count bar and a Tags button; Done turns it off", async ({ page }) => {
    await seed(page, "pickone");
    await seed(page, "picktwo");
    await reload(page);
    const total = (await clients(page)).length;
    await page.getByRole("button", { name: "Select", exact: true }).click();
    await expect(page.getByRole("button", { name: "Done", exact: true })).toBeVisible();
    await expect(page.getByRole("toolbar", { name: "Bulk download clients actions" })).toHaveCount(0);
    await page.getByRole("checkbox", { name: /^Select .*pickone/ }).check();
    const bar = page.getByRole("toolbar", { name: "Bulk download clients actions" });
    await expect(bar).toContainText("1 selected");
    await expect(bar.getByRole("button", { name: "Change tags on the selected download clients" })).toBeVisible();
    await page.getByRole("checkbox", { name: "Select all download clients" }).check();
    await expect(bar).toContainText(`${total} selected`);
    await page.getByRole("checkbox", { name: "Deselect all download clients" }).uncheck();
    await expect(bar).toHaveCount(0);
    await page.getByRole("checkbox", { name: /^Select .*pickone/ }).check();
    await expect(bar).toContainText("1 selected");
    await page.getByRole("button", { name: "Done", exact: true }).click();
    await expect(page.getByRole("checkbox", { name: /^Select all download clients/ })).toHaveCount(0);
    await expect(page.getByRole("toolbar", { name: "Bulk download clients actions" })).toHaveCount(0);
  });

  test("a new client's host and port presets fill the form, and Use it for libraries with no client yet is on and routes the client to a library that has none", async ({ page }) => {
    folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-real-dcl-"));
    const library = await api<{ id: string }>(page, "POST", "/api/libraries", { name: `No client ${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder });
    expect(library.status, JSON.stringify(library.body)).toBeLessThan(300);
    libraryId = library.body.id;
    await api(page, "PUT", `/api/libraries/${libraryId}/routing`, { sources: [], downloadClients: [] });
    await reload(page);
    await page.getByRole("button", { name: "New client" }).first().click();
    const drawer = page.getByRole("dialog");
    const use = drawer.getByRole("switch", { name: /^Use it for libraries with no client yet/ });
    await expect(use).toBeChecked();
    await drawer.getByRole("combobox", { name: "Client" }).selectOption({ label: "SABnzbd · Usenet" });
    const hostPick = drawer.getByRole("combobox").filter({ hasText: "This machine (localhost)" });
    expect(await hostPick.locator("option").allInnerTexts()).toEqual(["This machine (localhost)", "Localhost IPv4 (127.0.0.1)", "Docker host (host.docker.internal)", "Custom host / IP"]);
    await hostPick.selectOption({ label: "Custom host / IP" });
    await drawer.getByPlaceholder("Hostname or IP address").fill(hostOf("routed"));
    const portPick = drawer.getByRole("combobox").filter({ hasText: /default \(\d+\)/ }).first();
    await portPick.selectOption("6789");
    await drawer.getByRole("textbox", { name: /^API key/ }).fill("example-pw");
    await drawer.getByRole("switch", { name: /^Enabled/ }).click();
    await expect(drawer.getByRole("switch", { name: /^Enabled/ })).not.toBeChecked();
    await drawer.getByRole("button", { name: "Add client" }).click();
    await expect.poll(async () => (await clientAt(page, hostOf("routed")))?.port).toBe(6789);
    const created = await clientAt(page, hostOf("routed"));
    await expect.poll(async () => (await api<{ downloadClients: Array<{ downloadClientId: string }> }>(page, "GET", `/api/libraries/${libraryId}/routing`)).body.downloadClients.map((target) => target.downloadClientId)).toContain(created!.id);
  });
});
