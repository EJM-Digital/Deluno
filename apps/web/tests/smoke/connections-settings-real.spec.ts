import { mkdtempSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect as baseExpect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

const expect = baseExpect.configure({ timeout: 20_000 });

// Find & Download (Indexers, Proxies, Download Clients) against the real host: nothing here is stubbed. A person's clicks
// go through the real drawer to the real API and the real database, and each test reads the answer back from the server.
// The indexers and clients are only addresses nobody listens on; nothing here depends on another app's reply.

test.skip(({ isMobile }) => isMobile, "These drawers are the same on a phone.");

async function headers(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

type Row = Record<string, unknown> & { id: string; name: string };

async function list(page: Page, path: string): Promise<Row[]> {
  const response = await page.request.get(path, { headers: await headers(page) });
  expect(response.ok(), `GET ${path} answered ${response.status()}`).toBe(true);
  return await response.json() as Row[];
}

async function createIndexer(page: Page, name: string, over: Record<string, unknown> = {}) {
  const response = await page.request.post("/api/indexers", {
    headers: await headers(page),
    data: { name, protocol: "newznab", baseUrl: `https://${name.toLowerCase().replace(/[^a-z0-9]+/g, "-")}.example.test`, apiKey: "seed-key", privacy: "public", priority: 10, categories: "2000,5000", tags: "", mediaScope: "both", isEnabled: false, ...over }
  });
  expect(response.ok(), `POST /api/indexers answered ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Row;
}

test("a new Torznab indexer needs an API key; once saved it is never sent back, is not asked for again and a blank key on edit keeps it", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/indexers");
  const stamp = Date.now();
  const url = `https://torz-${stamp}.example.test`;
  await page.getByRole("button", { name: "New indexer" }).first().click();
  const drawer = page.getByRole("dialog", { name: "New indexer" });
  await drawer.getByLabel("Protocol").selectOption("torznab");
  await drawer.getByLabel("Torznab URL").fill(url);

  const before = (await list(page, "/api/indexers")).length;
  await drawer.getByRole("button", { name: "Add indexer" }).click();
  await expect(drawer.getByText("This indexer needs an API key.")).toBeVisible();
  expect((await list(page, "/api/indexers")).length).toBe(before);

  await drawer.getByLabel("API key").fill("typed-secret-123");
  await drawer.getByRole("button", { name: "Add indexer" }).click();
  await expect.poll(async () => (await list(page, "/api/indexers")).some((item) => String(item.baseUrl ?? "").includes(`torz-${stamp}`))).toBe(true);
  const saved = (await list(page, "/api/indexers")).find((item) => String(item.baseUrl ?? "").includes(`torz-${stamp}`))!;
  try {
    expect(JSON.stringify(saved), "the list must never carry the typed key").not.toContain("typed-secret-123");

    // The saved indexer's own drawer is open: a blank key is no longer asked for, and saving keeps the stored one.
    const edit = page.getByRole("dialog", { name: saved.name });
    await expect(edit).toBeVisible();
    await expect(edit.getByLabel("API key")).toHaveValue("");
    const renamed = `Torznab-${stamp}`;
    await edit.getByLabel("Name", { exact: true }).fill(renamed);
    const put = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/indexers/${saved.id}`);
    await edit.getByRole("button", { name: /^Save indexer$/ }).click();
    expect((await put).ok()).toBe(true);
    await expect(edit.getByText("This indexer needs an API key.")).toHaveCount(0);
    const after = (await list(page, "/api/indexers")).find((item) => item.id === saved.id)!;
    expect(after.name).toBe(renamed);
    expect(JSON.stringify(after)).not.toContain("typed-secret-123");
  } finally {
    await page.request.delete(`/api/indexers/${saved.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

test("the On switch in an indexer's row enables and pauses it on the server", async ({ page }) => {
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/indexers/indexers");
  const made = await createIndexer(page, `Switch-${Date.now()}`);
  try {
    await page.goto("/indexers/indexers");
    const row = page.getByRole("row").filter({ hasText: made.name }).first();
    const enable = row.getByRole("switch", { name: `Enable ${made.name}` });
    await expect(enable).not.toBeChecked();
    const put = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/indexers/${made.id}`);
    await enable.click();
    expect((await put).ok()).toBe(true);
    expect((await list(page, "/api/indexers")).find((item) => item.id === made.id)!.isEnabled).toBe(true);
    await expect(row.getByRole("switch", { name: `Pause ${made.name}` })).toBeChecked();

    const putAgain = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/indexers/${made.id}`);
    await row.getByRole("switch", { name: `Pause ${made.name}` }).click();
    expect((await putAgain).ok()).toBe(true);
    expect((await list(page, "/api/indexers")).find((item) => item.id === made.id)!.isEnabled).toBe(false);
  } finally {
    await page.request.delete(`/api/indexers/${made.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

test("a proxy is added from its drawer, saved, and removed with a confirmation that sends its indexers back to a direct connection", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/proxies");
  const stamp = Date.now();
  const address = `http://proxy-${stamp}.example.test:3128`;
  await page.getByRole("button", { name: "New proxy" }).first().click();
  const drawer = page.getByRole("dialog", { name: "New proxy" });
  await expect(drawer).toBeVisible();
  await drawer.getByLabel("Type").selectOption("http");
  await drawer.getByLabel("Address").fill(address);
  await drawer.getByRole("button", { name: "Add proxy" }).click();
  await expect(page.getByText("Proxy added").first()).toBeVisible();
  const proxy = (await list(page, "/api/indexer-proxies")).find((item) => JSON.stringify(item).includes(`proxy-${stamp}`))!;
  expect(proxy).toBeTruthy();
  const indexer = await createIndexer(page, `ViaProxy-${stamp}`, { proxyId: proxy.id });
  try {
    expect((await list(page, "/api/indexers")).find((item) => item.id === indexer.id)!.proxyId).toBe(proxy.id);

    // The drawer is now the saved proxy's own: change its address, save it, then remove it.
    const saved = page.getByRole("dialog", { name: new RegExp(`proxy-${stamp}`) });
    await expect(saved).toBeVisible();
    const moved = `http://moved-${stamp}.example.test:3128`;
    await saved.getByLabel("Address").fill(moved);
    const put = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/indexer-proxies/${proxy.id}`);
    await saved.getByRole("button", { name: /^Save proxy$/ }).click();
    expect((await put).ok()).toBe(true);
    expect((await list(page, "/api/indexer-proxies")).find((item) => item.id === proxy.id)!.address).toBe(moved);
    await page.getByRole("dialog", { name: new RegExp(`moved-${stamp}`) }).getByRole("button", { name: "Remove…" }).click();
    await page.getByRole("button", { name: "Remove proxy" }).click();
    await expect.poll(async () => (await list(page, "/api/indexer-proxies")).some((item) => item.id === proxy.id)).toBe(false);
    expect((await list(page, "/api/indexers")).find((item) => item.id === indexer.id)!.proxyId ?? null).toBeNull();
  } finally {
    await page.request.delete(`/api/indexers/${indexer.id}`, { headers: await headers(page) }).catch(() => undefined);
    await page.request.delete(`/api/indexer-proxies/${proxy.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

test("New client opens a drawer with defaults that are valid as they stand, and Add client keeps it open on the saved client", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/download-clients");
  await page.getByRole("button", { name: "New client" }).first().click();
  // A new client is named after its preset, so the drawer carries that name until the server names it from the address.
  const drawer = page.getByRole("dialog").filter({ has: page.getByLabel("Host or IP") });
  await expect(drawer).toBeVisible();
  await expect(drawer.getByLabel("Host or IP")).toHaveValue("localhost");
  const before = (await list(page, "/api/download-clients")).length;
  await drawer.getByRole("button", { name: "Add client" }).click();
  await expect(page.getByText("Client added").first()).toBeVisible();
  const clients = await list(page, "/api/download-clients");
  expect(clients.length).toBe(before + 1);
  const made = clients.at(-1)!;
  try {
    expect(made.host).toBe("localhost");
    await expect(drawer.getByRole("button", { name: /^Save client$/ })).toBeVisible();
  } finally {
    await page.request.delete(`/api/download-clients/${made.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

test("every indexer, proxy, client, media-server, Seerr and subtitle route refuses someone who is not signed in, and only the Seerr webhook is public", async ({ page }) => {
  await authenticateAndNavigate(page, "/indexers/indexers");
  const anonymous = await page.context().browser()!.newContext();
  try {
    for (const path of ["/api/indexers", "/api/indexer-proxies", "/api/download-clients", "/api/media-servers", "/api/seerr-servers", "/api/subtitle-providers", "/api/download-clients/history/dismissed"]) {
      const response = await anonymous.request.get(`${new URL(page.url()).origin}${path}`);
      expect(response.status(), `GET ${path} must refuse a request with no sign-in`).toBe(401);
    }
    for (const [method, path] of [["POST", "/api/indexers"], ["POST", "/api/download-clients"], ["POST", "/api/indexer-proxies"], ["POST", "/api/media-servers"], ["POST", "/api/seerr-servers"], ["DELETE", "/api/indexers/any"], ["DELETE", "/api/download-clients/any"]] as const) {
      const response = await anonymous.request.fetch(`${new URL(page.url()).origin}${path}`, { method, data: {} });
      expect(response.status(), `${method} ${path} must refuse a request with no sign-in`).toBe(401);
    }
    // The inbound Seerr webhook is the one public route: it answers for itself (refusing a call with no secret), never with 401.
    const webhook = await anonymous.request.post(`${new URL(page.url()).origin}/api/seerr-servers/any/webhook`, { data: {} });
    expect(webhook.status()).toBe(404);
  } finally {
    await anonymous.close();
  }
});

test("the Seerr webhook refuses a connection with no secret, a missing header and a wrong secret, and accepts the right one; a non-approval is acknowledged and adds nothing", async ({ page }) => {
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/indexers/media-servers");
  const created = await page.request.post("/api/seerr-servers", { headers: await headers(page), data: { name: "Lab Seerr", baseUrl: "http://127.0.0.1:5055", apiKey: "key-1", isEnabled: true } });
  expect(created.ok(), `POST /api/seerr-servers answered ${created.status()}: ${await created.text()}`).toBe(true);
  const server = (await created.json()) as Row;
  const anonymous = await page.context().browser()!.newContext();
  const url = `${new URL(page.url()).origin}/api/seerr-servers/${server.id}/webhook`;
  const approval = { notification_type: "MEDIA_APPROVED", event: "Movie Request Approved", media: { media_type: "movie", tmdbId: "603", media_id: 1 }, request: { requestedBy_username: "someone" } };
  try {
    // No secret has been made yet.
    expect((await anonymous.request.post(url, { data: approval, headers: { Authorization: "anything" } })).status()).toBe(401);
    const made = await page.request.post(`/api/seerr-servers/${server.id}/webhook-secret`, { headers: await headers(page) });
    expect(made.ok(), `webhook-secret answered ${made.status()}`).toBe(true);
    const body = (await made.json()) as Record<string, unknown>;
    const secret = String(Object.entries(body).find(([key, value]) => /secret/i.test(key) && typeof value === "string" && value.length > 0)?.[1] ?? "");
    expect(secret.length).toBeGreaterThan(8);

    expect((await anonymous.request.post(url, { data: approval })).status(), "a missing header").toBe(401);
    expect((await anonymous.request.post(url, { data: approval, headers: { Authorization: `${secret}-wrong` } })).status(), "a wrong secret").toBe(401);
    const accepted = await anonymous.request.post(url, { data: { notification_type: "MEDIA_PENDING", media: { media_type: "movie", media_id: 1 } }, headers: { Authorization: secret } });
    expect(accepted.status(), "the right secret").toBe(200);
    // An unknown connection answers for itself, not with a sign-in challenge: the route is public.
    expect((await anonymous.request.post(`${new URL(page.url()).origin}/api/seerr-servers/no-such/webhook`, { data: approval, headers: { Authorization: secret } })).status()).toBe(404);
  } finally {
    await anonymous.close();
    await page.request.delete(`/api/seerr-servers/${server.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

async function createClient(page: Page, host: string, over: Record<string, unknown> = {}) {
  const response = await page.request.post("/api/download-clients", {
    headers: await headers(page),
    data: { protocol: "qbittorrent", host, port: 8080, username: "u", password: "pw-do-not-show", moviesCategory: "movies", tvCategory: "tv", priority: 10, isEnabled: false, ...over }
  });
  expect(response.ok(), `POST /api/download-clients answered ${response.status()}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Row;
}

test("bulk Enable, Used for and Remove act on exactly the indexers that were ticked", async ({ page }) => {
  test.setTimeout(150_000);
  await authenticateAndNavigate(page, "/indexers/indexers");
  const stamp = Date.now();
  const made = [await createIndexer(page, `BulkA-${stamp}`), await createIndexer(page, `BulkB-${stamp}`), await createIndexer(page, `BulkC-${stamp}`)];
  try {
    await page.goto("/indexers/indexers");
    await page.getByRole("button", { name: "Select", exact: true }).first().click();
    await page.getByRole("checkbox", { name: `Select ${made[0].name}` }).click();
    await page.getByRole("checkbox", { name: `Select ${made[1].name}` }).click();
    const toolbar = page.getByRole("toolbar", { name: "Bulk indexer actions" });
    await expect(toolbar.getByText("2 selected")).toBeVisible();

    await toolbar.getByRole("button", { name: "Enable", exact: true }).click();
    await expect(page.getByText("2 indexers enabled")).toBeVisible();
    const afterEnable = await list(page, "/api/indexers");
    expect(afterEnable.find((item) => item.id === made[0].id)!.isEnabled).toBe(true);
    expect(afterEnable.find((item) => item.id === made[1].id)!.isEnabled).toBe(true);
    expect(afterEnable.find((item) => item.id === made[2].id)!.isEnabled).toBe(false);

    // A bulk action clears the selection; tick the same two again for the next one.
    await page.getByRole("checkbox", { name: `Select ${made[0].name}` }).click();
    await page.getByRole("checkbox", { name: `Select ${made[1].name}` }).click();
    await toolbar.getByLabel("Change used-for scope for selected indexers").selectOption("tv");
    await expect(page.getByText("2 indexers set to TV")).toBeVisible();
    const afterScope = await list(page, "/api/indexers");
    expect(afterScope.find((item) => item.id === made[0].id)!.mediaScope).toBe("tv");
    expect(afterScope.find((item) => item.id === made[2].id)!.mediaScope).toBe("both");

    await page.getByRole("checkbox", { name: `Select ${made[0].name}` }).click();
    await page.getByRole("checkbox", { name: `Select ${made[1].name}` }).click();
    await toolbar.getByRole("button", { name: "Remove", exact: true }).click();
    await expect(page.getByText("Remove 2 indexers?")).toBeVisible();
    await page.getByRole("button", { name: "Remove indexers" }).click();
    await expect.poll(async () => (await list(page, "/api/indexers")).filter((item) => made.some((m) => m.id === item.id)).map((item) => item.id)).toEqual([made[2].id]);
  } finally {
    for (const item of made) await page.request.delete(`/api/indexers/${item.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

test("a download client's row shows where it is used, its switch enables it, and its drawer links to the client's own page without the password", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/download-clients");
  const host = `own-page-${Date.now()}.example.test`;
  const made = await createClient(page, host, { port: 8181 });
  try {
    await page.goto("/indexers/download-clients");
    const row = page.getByRole("row").filter({ hasText: host }).first();
    await expect(row).toContainText(host);
    const put = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/download-clients/${made.id}`);
    await row.getByRole("switch", { name: /^Enable / }).click();
    expect((await put).ok()).toBe(true);
    expect((await list(page, "/api/download-clients")).find((item) => item.id === made.id)!.isEnabled).toBe(true);

    await row.click();
    const drawer = page.getByRole("dialog").filter({ has: page.getByLabel("Host or IP") });
    const link = drawer.getByRole("link", { name: /page$/ });
    await expect(link).toBeVisible();
    const href = await link.getAttribute("href");
    expect(href).toContain(`${host}:8181`);
    expect(href).not.toContain("pw-do-not-show");
    expect(await link.getAttribute("target")).toBe("_blank");
  } finally {
    await page.request.delete(`/api/download-clients/${made.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

test("a remote path mapping is added from the form once both paths are typed, listed, and kept by the server", async ({ page }) => {
  test.setTimeout(120_000);
  await authenticateAndNavigate(page, "/indexers/download-clients");
  const host = `mapping-${Date.now()}.example.test`;
  const made = await createClient(page, host);
  try {
    await page.goto("/indexers/path-mappings");
    const add = page.getByRole("button", { name: "Add mapping" });
    await expect(add).toBeDisabled();
    await page.getByLabel("Download client this mapping is filed under").selectOption(made.id);
    await page.getByLabel("Client path").fill("/downloads/complete");
    await expect(add).toBeDisabled();
    await page.getByLabel("Deluno path").fill("/mnt/deluno/complete");
    await expect(add).toBeEnabled();
    await add.click();
    await expect(page.getByText("Remote path mapping added").first()).toBeVisible();
    await expect(page.getByLabel("Client path")).toHaveValue("");
    await expect(page.getByRole("row").filter({ hasText: "/downloads/complete" }).first()).toBeVisible();
    const stored = await list(page, `/api/download-clients/${made.id}/path-mappings`);
    expect(stored.some((item) => item.remotePath === "/downloads/complete" && item.localPath === "/mnt/deluno/complete")).toBe(true);
  } finally {
    await page.request.delete(`/api/download-clients/${made.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

test("Library Routing saves the indexer and client a library uses and the server keeps them", async ({ page }) => {
  test.setTimeout(150_000);
  await authenticateAndNavigate(page, "/indexers/library-routing");
  const stamp = Date.now();
  const folder = mkdtempSync(path.join(os.tmpdir(), "deluno-routing-"));
  const library = await page.request.post("/api/libraries", { headers: await headers(page), data: { name: `Routing-${stamp}`, mediaType: "movies", purpose: "collection", rootPath: folder, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false } });
  expect(library.ok(), await library.text()).toBe(true);
  const lib = (await library.json()) as Row;
  const indexer = await createIndexer(page, `RouteIdx-${stamp}`, { mediaScope: "movies", isEnabled: true });
  const client = await createClient(page, `route-${stamp}.example.test`, { isEnabled: true });
  try {
    // Start from nothing routed, so the Save below is what puts them there.
    await page.request.put(`/api/libraries/${lib.id}/routing`, { headers: await headers(page), data: { sources: [], downloadClients: [] } });
    await page.goto("/indexers/library-routing");
    await page.getByRole("row").filter({ hasText: `Routing-${stamp}` }).first().click();
    const drawer = page.getByRole("dialog").filter({ hasText: "Save routing" });
    await expect(drawer).toBeVisible();
    await drawer.getByRole("switch", { name: new RegExp(`RouteIdx-${stamp}`) }).click();
    await drawer.getByRole("switch", { name: new RegExp(`route-${stamp}`) }).click();
    await drawer.getByRole("button", { name: "Save routing" }).click();
    await expect(drawer.getByText("Routing saved")).toBeVisible();
    const routing = (await (await page.request.get(`/api/libraries/${lib.id}/routing`, { headers: await headers(page) })).json()) as { sources: { indexerId: string }[]; downloadClients: { downloadClientId: string }[] };
    expect(routing.sources.map((item) => item.indexerId)).toEqual([indexer.id]);
    expect(routing.downloadClients.map((item) => item.downloadClientId)).toEqual([client.id]);
  } finally {
    await page.request.delete(`/api/libraries/${lib.id}`, { headers: await headers(page) }).catch(() => undefined);
    await page.request.delete(`/api/indexers/${indexer.id}`, { headers: await headers(page) }).catch(() => undefined);
    await page.request.delete(`/api/download-clients/${client.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});

test("the Find & Download screens open without an application error, and a deep link opens the client it names", async ({ page }) => {
  test.setTimeout(120_000);
  for (const route of ["/indexers/indexers", "/indexers/download-clients", "/indexers/library-routing", "/indexers/proxies", "/indexers/path-mappings", "/indexers/media-servers", "/indexers/subtitle-providers"]) {
    await authenticateAndNavigate(page, route);
    await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
  }
  const host = `deep-${Date.now()}.example.test`;
  const made = await createClient(page, host);
  try {
    await page.goto(`/indexers/download-clients?connection=${made.id}`);
    const drawer = page.getByRole("dialog").filter({ has: page.getByLabel("Host or IP") });
    await expect(drawer.getByLabel("Custom host / IP")).toHaveValue(host);
    await expect.poll(() => new URL(page.url()).search).toBe("");
  } finally {
    await page.request.delete(`/api/download-clients/${made.id}`, { headers: await headers(page) }).catch(() => undefined);
  }
});
