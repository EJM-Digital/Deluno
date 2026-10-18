import { mkdirSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect as baseExpect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { loadRecording, startRecordedAppServer } from "../helpers/recorded-app-server";
import { untilQuiet } from "../helpers/settle";

const expect = baseExpect.configure({ timeout: 20_000 });

// Find & Download against the real host. A person's clicks go through the real drawers to the real API and database, and each
// result is read back from the server. Only three things are stood in for, each said where it is: the answer of a built-in
// tracker's Test (the tracker is a site on the internet), a list route made to fail once (to see the error screen), and the
// recorded replies of Jackett 0.24.2850 served over a real loopback port (tests/fixtures/recorded).

test.skip(({ isMobile }) => isMobile, "These screens and drawers are the same on a phone.");

type Row = Record<string, unknown> & { id: string; name: string };

async function headers(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function list(page: Page, route: string): Promise<Row[]> {
  const response = await page.request.get(route, { headers: await headers(page) });
  expect(response.ok(), `GET ${route} answered ${response.status()}`).toBe(true);
  return await response.json() as Row[];
}

async function create(page: Page, route: string, data: Record<string, unknown>): Promise<Row> {
  const response = await page.request.post(route, { headers: await headers(page), data });
  expect(response.ok(), `POST ${route} answered ${response.status()}: ${await response.text()}`).toBe(true);
  return await response.json() as Row;
}

async function remove(page: Page, route: string) {
  await page.request.delete(route, { headers: await headers(page) }).catch(() => undefined);
}

test("an indexer's Used for cell says which libraries search it, or links to Library Routing when none does", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/indexers");
  const stamp = Date.now();
  const root = path.join(os.tmpdir(), "deluno-playwright-libraries", `usedfor-${stamp}`);
  mkdirSync(root, { recursive: true });
  const libraryName = `Used-for-Films-${stamp}`;
  const library = await create(page, "/api/libraries", { name: libraryName, mediaType: "movies", purpose: "collection", rootPath: root, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false });
  const base = { protocol: "torznab", privacy: "public", priority: 10, categories: "2000", tags: "", mediaScope: "movies", isEnabled: false, apiKey: "seed-key" };
  const routed = await create(page, "/api/indexers", { ...base, name: `Routed-${stamp}`, baseUrl: `https://routed-${stamp}.example.test` });
  const alone = await create(page, "/api/indexers", { ...base, name: `Alone-${stamp}`, baseUrl: `https://alone-${stamp}.example.test` });
  try {
    // The server switches a new indexer on in every library of its kind; this one library is then left searching only the first.
    const saved = await page.request.put(`/api/libraries/${library.id}/routing`, {
      headers: await headers(page),
      data: { sources: [{ indexerId: routed.id, priority: 1, requiredTags: "", excludedTags: "" }], downloadClients: [] }
    });
    expect(saved.ok(), await saved.text()).toBe(true);

    await page.goto("/indexers/indexers");
    const routedRow = page.getByRole("row").filter({ hasText: routed.name }).first();
    await expect(routedRow).toContainText(`Searched by ${libraryName}`);
    const aloneRow = page.getByRole("row").filter({ hasText: alone.name }).first();
    await expect(aloneRow).toContainText("No library searches it");
    await aloneRow.getByText(/Switch it on in Library Routing/).click();
    await expect(page).toHaveURL(/\/indexers\/library-routing/);
  } finally {
    await remove(page, `/api/indexers/${routed.id}`);
    await remove(page, `/api/indexers/${alone.id}`);
    await remove(page, `/api/libraries/${library.id}`);
  }
});

test("a server that refuses a new indexer's request interval has its first message shown in the drawer, and nothing is saved", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/indexers");
  const before = (await list(page, "/api/indexers")).map((item) => item.id);
  const host = `interval-${Date.now()}.example.test`;
  await page.getByRole("button", { name: "New indexer" }).first().click();
  const drawer = page.getByRole("dialog", { name: "New indexer" });
  await drawer.getByLabel("Protocol").selectOption("torznab");
  await drawer.getByLabel("Torznab URL").fill(`https://${host}`);
  await drawer.getByLabel("API key").fill("draft-key");
  await drawer.getByRole("button", { name: /^Fine-tune/ }).click();
  await drawer.getByLabel(/^Request interval/).fill("1");

  await drawer.getByRole("button", { name: "Add indexer" }).click();

  await expect(drawer.getByText("The request interval must be between 2 and 60 seconds. Deluno will not query an indexer more often than once every 2 seconds.").first()).toBeVisible();
  expect((await list(page, "/api/indexers")).map((item) => item.id)).toEqual(before);
  await expect(drawer).toBeVisible();
});

test("a healthy test of a new Torznab indexer fills in its categories and Used for from the indexer's own capabilities, and both can still be edited", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/indexers");
  const caps = loadRecording("jackett", "0.24.2850", "caps");
  const recordedIds = [...(caps.exchanges[0]!.reply.bodyText ?? "").matchAll(/<(?:category|subcat) id="(\d+)"/g)].map((match) => match[1]!);
  expect(recordedIds.length).toBeGreaterThan(3);
  const jackett = await startRecordedAppServer([caps]);
  const before = (await list(page, "/api/indexers")).map((item) => item.id);
  let madeId: string | undefined;
  try {
    await page.getByRole("button", { name: "New indexer" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New indexer" });
    await drawer.getByLabel("Protocol").selectOption("torznab");
    await drawer.getByLabel("Torznab URL").fill(`${jackett.baseUrl}/api/v2.0/indexers/yts/results/torznab/api`);
    await drawer.getByLabel("API key").fill("0123456789abcdef0123456789abcdef");
    await drawer.getByRole("radio", { name: "Both", exact: true }).click();
    await drawer.getByRole("button", { name: "Test", exact: true }).click();

    // Jackett's tracker answers the film search and not the TV search: Used for follows, and the category ids are the tracker's own.
    await expect(drawer.getByRole("radio", { name: "Movies", exact: true })).toBeChecked();
    await drawer.getByRole("button", { name: /^Categories/ }).click();
    const ids = drawer.getByLabel(/^Category ids/);
    await expect.poll(async () => (await ids.inputValue()).split(",").map((item) => item.trim()).filter(Boolean).sort()).toEqual([...new Set(recordedIds)].sort());

    // Editable afterwards.
    await ids.fill("2000,2040");
    await drawer.getByRole("button", { name: "Add indexer" }).click();
    await expect.poll(async () => (await list(page, "/api/indexers")).some((item) => !before.includes(item.id))).toBe(true);
    const made = (await list(page, "/api/indexers")).find((item) => !before.includes(item.id))!;
    madeId = made.id;
    expect(made).toMatchObject({ protocol: "torznab", mediaScope: "movies" });
    expect(String(made.categories).split(",").map((item) => item.trim())).toEqual(["2000", "2040"]);
  } finally {
    if (madeId) await remove(page, `/api/indexers/${madeId}`);
    await jackett.close();
  }
});

test("adding a built-in tracker: pick it, fill it in, Test it, Add it, and it is a saved tracker that can be removed", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/indexers");
  const before = (await list(page, "/api/indexers")).map((item) => item.id);
  let madeId: string | undefined;
  try {
    // Only the answer of the Test is stood in for: the tracker is a site on the internet.
    await page.route("**/api/indexers/test", (route) => route.fulfill({ json: { healthStatus: "healthy", message: "Reached the site and read its search page.", latencyMs: 40 } }));
    await page.getByRole("button", { name: "New indexer" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New indexer" });
    await drawer.getByLabel("Protocol").selectOption("tracker");
    await drawer.getByRole("textbox", { name: "Search trackers" }).fill("Nyaa");
    await drawer.getByRole("listbox", { name: "Trackers" }).getByRole("option", { name: /Nyaa/i }).first().click();
    await expect(drawer.getByText(/Nyaa/).first()).toBeVisible();

    await drawer.getByRole("button", { name: "Test", exact: true }).click();
    await expect(drawer.getByText("Reached the site and read its search page.")).toBeVisible();
    await drawer.getByRole("button", { name: "Add indexer" }).click();

    await expect.poll(async () => (await list(page, "/api/indexers")).some((item) => !before.includes(item.id))).toBe(true);
    const made = (await list(page, "/api/indexers")).find((item) => !before.includes(item.id))!;
    madeId = made.id;
    expect(made).toMatchObject({ protocol: "tracker" });
    expect(String(made.definitionId ?? "")).toMatch(/nyaa/i);
    await page.unroute("**/api/indexers/test");

    await page.goto("/indexers/indexers");
    await page.getByRole("row").filter({ hasText: made.name }).first().click();
    const saved = page.getByRole("dialog", { name: made.name });
    await saved.getByRole("button", { name: "Remove…" }).click();
    await page.getByRole("alertdialog").or(page.getByRole("dialog", { name: /^Remove /i })).getByRole("button", { name: "Remove indexer" }).click();
    await expect.poll(async () => (await list(page, "/api/indexers")).some((item) => item.id === madeId)).toBe(false);
    madeId = undefined;
  } finally {
    if (madeId) await remove(page, `/api/indexers/${madeId}`);
  }
});

for (const source of [
  { name: "indexers", label: "Indexers", route: /\/api\/indexers$/ },
  { name: "download clients", label: "Download clients", route: /\/api\/download-clients$/ },
  { name: "libraries", label: "Libraries", route: /\/api\/libraries$/ }
] as const) {
  test(`when the list of ${source.name} cannot be read, Find & Download says so in the screen instead of drawing a blank page, and Retry recovers once it can`, async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    // The one answer made to fail: the server's list of this kind. Everything else is the real host.
    await page.route(source.route, (route) => route.request().method() === "GET" ? route.fulfill({ status: 500, contentType: "application/json", body: JSON.stringify({ title: "Server error" }) }) : route.continue());
    await page.goto("/indexers/indexers");
    const alert = page.getByRole("alert").filter({ hasText: "This screen could not load everything it needs." });
    await expect(alert).toContainText(source.label);
    await expect(alert).toContainText("could not be read.");
    await expect(page.getByRole("button", { name: "New indexer" })).toHaveCount(0);

    await page.unroute(source.route);
    await alert.getByRole("button", { name: "Retry" }).click();
    await expect(page.getByRole("button", { name: "New indexer" }).first()).toBeVisible();
    await expect(page.getByRole("alert").filter({ hasText: "could not load everything" })).toHaveCount(0);
  });
}

test.describe("Find & Download follows the server and asks only for the lists an event moves", () => {
  async function push(page: Page, eventName: string, subject: "dashboard" | "live" = "dashboard") {
    const status = await page.evaluate(async ({ eventName, subject }) => {
      const token = localStorage.getItem("deluno-auth-token");
      const response = await fetch("/api/testing/realtime/event", { method: "POST", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, body: JSON.stringify({ eventName, subject, payload: {} }) });
      return response.status;
    }, { eventName, subject });
    expect(status).toBe(204);
  }

  async function watching(page: Page, subject: "dashboard" | "live") {
    await expect.poll(async () => page.evaluate(async (name) => {
      const token = localStorage.getItem("deluno-auth-token");
      const response = await fetch("/api/testing/realtime/watchers", { headers: { Authorization: `Bearer ${token}` } });
      return ((await response.json()) as Record<string, number>)[name] ?? 0;
    }, subject), { timeout: 15_000, message: `the page never joined the ${subject} subject` }).toBeGreaterThan(0);
  }

  const events = [
    { event: "IndexerChanged", subject: "dashboard", asks: "/api/indexers", never: ["/api/download-clients", "/api/settings"] },
    { event: "DownloadClientChanged", subject: "dashboard", asks: "/api/download-clients", never: ["/api/settings"] },
    { event: "HealthChanged", subject: "dashboard", asks: "/api/indexers", also: "/api/download-clients", never: ["/api/settings", "/api/libraries"] },
    { event: "ConnectionChecked", subject: "dashboard", asks: "/api/indexers", also: "/api/download-clients", never: ["/api/settings", "/api/libraries"] },
    { event: "LibraryChanged", subject: "dashboard", asks: "/api/libraries", never: ["/api/indexers", "/api/download-clients"] },
    { event: "SettingsChanged", subject: "dashboard", asks: "/api/settings", never: ["/api/indexers", "/api/download-clients", "/api/libraries"] },
    { event: "ClientStateChanged", subject: "live", asks: "/api/download-clients", never: ["/api/settings", "/api/libraries"] }
  ] as const;

  for (const entry of events) {
    test(`${entry.event} reads ${entry.asks}${"also" in entry ? ` and ${entry.also}` : ""} again and nothing else, with no reload`, async ({ page }) => {
      test.setTimeout(90_000);
      await authenticateAndNavigate(page, "/");
      let navigations = 0;
      page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) navigations += 1; });
      await page.goto("/indexers/library-routing");
      await expect(page.getByRole("button", { name: "New indexer" }).first().or(page.getByText("Library Routing").first())).toBeVisible();
      await watching(page, entry.subject);
      await untilQuiet(page);
      const afterLoad = navigations;
      const asked = new Set<string>();
      page.on("request", (request) => { if (request.method() === "GET") asked.add(new URL(request.url()).pathname); });

      await push(page, entry.event, entry.subject);

      await expect.poll(() => asked.has(entry.asks)).toBe(true);
      if ("also" in entry) await expect.poll(() => asked.has(entry.also)).toBe(true);
      await page.waitForTimeout(1500); // reason: a list an event does not move would be asked for within a moment of the ones it does; there is nothing to wait on for a request that must not come
      for (const never of entry.never) expect([...asked], `${entry.event} asked for ${never}`).not.toContain(never);
      expect(navigations, "the page reloaded or navigated").toBe(afterLoad);
    });
  }
});

test("a remote path mapping is edited from its row: the form fills from the row, Save changes puts the replacement in and takes the original out, and a failed save loses nothing", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/path-mappings");
  const host = `edit-map-${Date.now()}.example.test`;
  const client = await create(page, "/api/download-clients", { protocol: "qbittorrent", host, port: 8080, username: "deluno", password: "pw", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false });
  const mappings = async () => (await page.request.get(`/api/download-clients/path-mappings`, { headers: await headers(page) }).then((response) => response.json()) as Array<Record<string, unknown> & { id: string }>).filter((item) => item.downloadClientId === client.id);
  try {
    const first = await page.request.post(`/api/download-clients/${client.id}/path-mappings`, { headers: await headers(page), data: { remotePath: "/downloads/complete", localPath: "D:\\Downloads\\complete", isEnabled: true, priority: 10, host } });
    expect(first.ok(), await first.text()).toBe(true);
    await page.goto("/indexers/path-mappings");
    const row = page.getByRole("row").filter({ hasText: "/downloads/complete" }).filter({ hasText: host }).first();
    await expect(row).toBeVisible();

    // Edit: the form fills from the row.
    await row.getByRole("button", { name: "Edit", exact: true }).click();
    await expect(page.getByLabel("Client path")).toHaveValue("/downloads/complete");
    await expect(page.getByLabel("Deluno path")).toHaveValue("D:\\Downloads\\complete");

    // A save the server refuses leaves the original exactly as it was. Only that one answer is made to fail.
    await page.getByLabel("Client path").fill("/downloads/changed");
    await page.route(/\/api\/download-clients\/[^/]+\/path-mappings$/, (route) => route.request().method() === "POST" ? route.fulfill({ status: 500, contentType: "application/json", body: JSON.stringify({ title: "Server error" }) }) : route.continue());
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Server error").first()).toBeVisible();
    expect((await mappings()).map((item) => item.remotePath)).toEqual(["/downloads/complete"]);
    await page.unroute(/\/api\/download-clients\/[^/]+\/path-mappings$/);

    // Save changes: the replacement first, then the original goes.
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect.poll(async () => (await mappings()).map((item) => item.remotePath)).toEqual(["/downloads/changed"]);
    await expect(page.getByRole("button", { name: "Add mapping" })).toBeVisible();

    // Cancel puts the form back to blank without touching the row.
    await page.getByRole("row").filter({ hasText: "/downloads/changed" }).first().getByRole("button", { name: "Edit", exact: true }).click();
    await page.getByRole("button", { name: "Cancel" }).click();
    await expect(page.getByLabel("Client path")).toHaveValue("");
    expect((await mappings()).map((item) => item.remotePath)).toEqual(["/downloads/changed"]);
  } finally {
    await remove(page, `/api/download-clients/${client.id}`);
  }
});
