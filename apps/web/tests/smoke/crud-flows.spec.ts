/**
 * CRUD flow smoke tests.
 *
 * These tests use page.request (authenticated via Bearer token extracted from
 * sessionStorage after browser login) to set up data via the API, then verify
 * the UI renders and responds correctly.
 * This is more reliable than testing form submission directly, which is brittle
 * against label/placeholder changes.
 */
import { expect, test } from "@playwright/test";
import { mkdirSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fallbackCredentials } from "../helpers/auth-helper";

let credentials: { username: string; password: string } | null = null;
let authToken: string | null = null;

test.describe("indexer and download client CRUD", () => {
  test.beforeAll(async ({ request }) => {
    const statusResponse = await request.get("/api/auth/bootstrap-status");
    const status = statusResponse.ok()
      ? ((await statusResponse.json()) as { requiresSetup?: boolean })
      : { requiresSetup: false };

    if (status.requiresSetup) {
      const bootstrap = await request.post("/api/auth/bootstrap", {
        data: fallbackCredentials
      });
      if (bootstrap.ok()) {
        credentials = fallbackCredentials;
        return;
      }
    }

    const fallbackLogin = await request.post("/api/auth/login", {
      data: { username: fallbackCredentials.username, password: fallbackCredentials.password }
    });
    if (fallbackLogin.ok()) {
      credentials = fallbackCredentials;
      return;
    }

    if (process.env.DELUNO_E2E_USERNAME && process.env.DELUNO_E2E_PASSWORD) {
      credentials = {
        username: process.env.DELUNO_E2E_USERNAME,
        password: process.env.DELUNO_E2E_PASSWORD
      };
    }
  });

  test.beforeEach(async ({ page }) => {
    test.skip(!credentials, "Set DELUNO_E2E_USERNAME and DELUNO_E2E_PASSWORD to run CRUD tests against an existing install.");

    await page.goto("/login");
    await page.getByLabel(/username/i).fill(credentials!.username);
    await page.getByLabel("Password", { exact: true }).fill(credentials!.password);
    await page.getByRole("button", { name: /sign in/i }).click();
    await expect(page).not.toHaveURL(/\/login/);

    // The app stores auth in sessionStorage (not cookies), so page.request calls
    // need an explicit Authorization header. Extract the token here for test use.
    authToken = await page.evaluate(() => sessionStorage.getItem("deluno-auth-token"));
  });

  function authHeaders(): Record<string, string> {
    return authToken ? { Authorization: `Bearer ${authToken}` } : {};
  }

  // ── Indexer CRUD ──────────────────────────────────────────────────────────

  test("indexer created via API appears on the indexers page", async ({ page }) => {
    const uniqueName = `Smoke-Indexer-${Date.now()}`;

    const createResp = await page.request.post("/api/indexers", {
      data: {
        name: uniqueName,
        protocol: "torznab",
        privacy: "private",
        baseUrl: "https://smoke-indexer.example.test",
        apiKey: null,
        priority: 10,
        categories: "2000",
        tags: "",
        mediaScope: "movies",
        isEnabled: true
      },
      headers: authHeaders()
    });
    expect(createResp.ok(), `POST /api/indexers failed: ${createResp.status()}`).toBe(true);
    const indexer = await createResp.json() as { id: string };

    try {
      await page.goto("/indexers/indexers");
      await expect(page.getByText(uniqueName).first()).toBeVisible();
    } finally {
      // Cleanup
      await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    }
  });

  test("indexer deleted via API disappears from the indexers page", async ({ page }) => {
    const uniqueName = `Smoke-Del-${Date.now()}`;

    const createResp = await page.request.post("/api/indexers", {
      data: {
        name: uniqueName,
        protocol: "rss",
        privacy: "public",
        baseUrl: "https://smoke-rss.example.test",
        apiKey: null,
        priority: 50,
        categories: "",
        tags: "",
        mediaScope: "both",
        isEnabled: false
      },
      headers: authHeaders()
    });
    expect(createResp.ok()).toBe(true);
    const indexer = await createResp.json() as { id: string };

    // Verify it shows
    await page.goto("/indexers/indexers");
    await expect(page.getByText(uniqueName).first()).toBeVisible();

    // Delete via API
    const deleteResp = await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    expect(deleteResp.ok()).toBe(true);

    // Reload and verify gone
    await page.reload();
    await expect(page.getByText(uniqueName)).toHaveCount(0);
  });

  test("sources page opens the indexer drawer with every protocol available", async ({ page }) => {
    await page.goto("/indexers/indexers");
    const addButton = page.getByRole("button", { name: "New indexer" }).first();
    await expect(addButton).toBeVisible();

    await addButton.click();
    const drawer = page.getByRole("dialog", { name: "New indexer" });
    await expect(drawer).toBeVisible();
    const protocol = drawer.getByLabel("Protocol");
    await expect(protocol.locator("option", { hasText: "Torznab" })).toHaveCount(1);
    await expect(protocol.locator("option", { hasText: "Newznab" })).toHaveCount(1);
    await expect(protocol.locator("option", { hasText: "RSS feed" })).toHaveCount(1);
  });

  test("indexer drawer shows URL and scope fields for the chosen protocol", async ({ page }) => {
    await page.goto("/indexers/indexers");
    await page.getByRole("button", { name: "New indexer" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New indexer" });
    await drawer.getByLabel("Protocol").selectOption("torznab");

    await expect(drawer.getByRole("radiogroup", { name: "Used for" })).toBeVisible();
    await expect(drawer.getByPlaceholder(/localhost:9117/i)).toBeVisible();
    await page.keyboard.press("Escape");
  });

  test("updated indexer fields are returned correctly by the API", async ({ page }) => {
    const uniqueName = `Smoke-Update-${Date.now()}`;

    const createResp = await page.request.post("/api/indexers", {
      data: {
        name: uniqueName,
        protocol: "rss",
        privacy: "public",
        baseUrl: "https://before.example.test",
        apiKey: null,
        priority: 10,
        categories: "",
        tags: "",
        mediaScope: "movies",
        isEnabled: true
      },
      headers: authHeaders()
    });
    expect(createResp.ok()).toBe(true);
    const indexer = await createResp.json() as { id: string; mediaScope: string };

    try {
      // Update only the name — mediaScope must be preserved (regression for the newScope PATCH bug)
      const updateResp = await page.request.put(`/api/indexers/${indexer.id}`, {
        data: {
          name: `${uniqueName}-renamed`,
          protocol: null,
          privacy: null,
          baseUrl: null,
          apiKey: null,
          priority: null,
          categories: null,
          tags: null,
          mediaScope: null,
          isEnabled: null
        },
        headers: authHeaders()
      });
      expect(updateResp.ok(), `PUT /api/indexers/${indexer.id} failed: ${updateResp.status()}`).toBe(true);

      const updated = await updateResp.json() as { name: string; mediaScope: string };
      expect(updated.name).toBe(`${uniqueName}-renamed`);
      expect(updated.mediaScope).toBe("movies"); // must be unchanged — was broken before the fix
    } finally {
      await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    }
  });

  test("indexer request intervals are persisted, resettable, and reject unsafe values", async ({ page }) => {
    const uniqueName = `Smoke-Interval-${Date.now()}`;
    const createResp = await page.request.post("/api/indexers", {
      data: {
        name: uniqueName,
        protocol: "rss",
        privacy: "public",
        baseUrl: "https://interval.example.test",
        priority: 10,
        categories: "",
        tags: "",
        mediaScope: "movies",
        isEnabled: true,
        requestIntervalSeconds: 10
      },
      headers: authHeaders()
    });
    expect(createResp.ok()).toBe(true);
    const indexer = await createResp.json() as { id: string; requestIntervalSeconds: number | null };
    expect(indexer.requestIntervalSeconds).toBe(10);

    try {
      const resetResp = await page.request.put(`/api/indexers/${indexer.id}`, {
        data: { clearRequestInterval: true },
        headers: authHeaders()
      });
      expect(resetResp.ok()).toBe(true);
      expect((await resetResp.json() as { requestIntervalSeconds: number | null }).requestIntervalSeconds).toBeNull();

      const invalidResp = await page.request.post("/api/indexers", {
        data: { name: `${uniqueName}-invalid`, baseUrl: "https://invalid.example.test", isEnabled: true, requestIntervalSeconds: 1 },
        headers: authHeaders()
      });
      expect(invalidResp.status()).toBe(400);
      expect(JSON.stringify(await invalidResp.json())).toContain("between 2 and 60 seconds");
    } finally {
      await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    }
  });

  test("editing an indexer in the drawer keeps its identity", async ({ page }) => {
    const name = `Smoke-IndexerDrawer-${Date.now()}`;
    const updatedUrl = "https://after-indexer.example.test/feed.rss";
    const create = await page.request.post("/api/indexers", {
      data: { name, protocol: "rss", privacy: "public", baseUrl: "https://before-indexer.example.test/feed.rss", priority: 10, categories: "", tags: "", mediaScope: "movies", isEnabled: true },
      headers: authHeaders()
    });
    expect(create.ok()).toBe(true);
    const indexer = await create.json() as { id: string };

    try {
      await page.goto("/indexers/indexers");
      await page.getByRole("row").filter({ hasText: name }).click();
      const drawer = page.getByRole("dialog", { name });
      await expect(drawer.getByLabel("URL")).toHaveValue("https://before-indexer.example.test/feed.rss");
      await drawer.getByLabel("URL").fill(updatedUrl);
      const update = page.waitForRequest((request) => request.method() === "PUT" && new URL(request.url()).pathname === `/api/indexers/${indexer.id}`);
      await drawer.getByRole("button", { name: "Save indexer" }).click();
      await update;
      const list = await page.request.get("/api/indexers", { headers: authHeaders() });
      expect(list.ok()).toBe(true);
      expect((await list.json() as Array<{ id: string; baseUrl: string }>).find((item) => item.id === indexer.id)).toMatchObject({ id: indexer.id, baseUrl: updatedUrl });
    } finally {
      await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    }
  });

  test("shows when Deluno is proactively pacing an indexer", async ({ page }, testInfo) => {
    const uniqueName = `Smoke-Pacing-${Date.now()}`;
    const host = "pacing-indexer.example.test";
    const createResp = await page.request.post("/api/indexers", {
      data: {
        name: uniqueName,
        protocol: "rss",
        privacy: "public",
        baseUrl: `https://${host}/feed.rss`,
        priority: 10,
        categories: "",
        isEnabled: true
      },
      headers: authHeaders()
    });
    expect(createResp.ok(), `POST /api/indexers failed: ${createResp.status()}`).toBe(true);
    const indexer = await createResp.json() as { id: string };

    try {
      const snapshot = { hosts: [{ host, waiting: 2, grantedCount: 4, refusedCount: 0, totalWaitedSeconds: 3.5, nextPermitInSeconds: 1.2 }] };
      await page.addInitScript((nextSnapshot) => {
        const originalFetch = window.fetch.bind(window);
        window.fetch = ((input: RequestInfo | URL, init?: RequestInit) => {
          const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
          if (new URL(url, window.location.origin).pathname === "/api/integrations/outbound-throttle") {
            return Promise.resolve(new Response(JSON.stringify(nextSnapshot), { headers: { "Content-Type": "application/json" } }));
          }
          return originalFetch(input, init);
        }) as typeof window.fetch;
      }, snapshot);

      await page.goto("/indexers");
      const row = page.getByRole("row").filter({ hasText: uniqueName });
      if (testInfo.project.name !== "mobile") {
        await expect(row.getByText("2 requests waiting", { exact: true })).toBeVisible();
        await expect(row.getByText(`Deluno is pacing ${host}`, { exact: true })).toBeVisible();
      }

      await row.click();
      const drawer = page.getByRole("dialog", { name: uniqueName });
      await expect(drawer.getByText(`Deluno is waiting on ${host} before sending 2 requests.`, { exact: true })).toBeVisible();
    } finally {
      await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    }
  });

  // ── Download client CRUD ──────────────────────────────────────────────────

  test("download client created via API appears on the indexers page", async ({ page }) => {
    const uniqueName = `Smoke-Client-${Date.now()}`;

    const createResp = await page.request.post("/api/download-clients", {
      data: {
        name: uniqueName,
        protocol: "qbittorrent",
        host: "localhost",
        port: 8080,
        username: null,
        password: null,
        endpointUrl: null,
        moviesCategory: "smoke-movies",
        tvCategory: "smoke-tv",
        categoryTemplate: null,
        priority: 1,
        isEnabled: false
      },
      headers: authHeaders()
    });
    expect(createResp.ok(), `POST /api/download-clients failed: ${createResp.status()}`).toBe(true);
    const client = await createResp.json() as { id: string };

    try {
      await page.goto("/indexers/download-clients");
      await expect(page.getByText(uniqueName).first()).toBeVisible();
    } finally {
      await page.request.delete(`/api/download-clients/${client.id}`, { headers: authHeaders() });
    }
  });

  test("download client deleted via API disappears from the indexers page", async ({ page }) => {
    const uniqueName = `Smoke-ClientDel-${Date.now()}`;

    const createResp = await page.request.post("/api/download-clients", {
      data: {
        name: uniqueName,
        protocol: "transmission",
        host: "localhost",
        port: 9091,
        username: null,
        password: null,
        endpointUrl: null,
        moviesCategory: "deluno-movies",
        tvCategory: "deluno-tv",
        categoryTemplate: null,
        priority: 1,
        isEnabled: false
      },
      headers: authHeaders()
    });
    expect(createResp.ok()).toBe(true);
    const client = await createResp.json() as { id: string };

    await page.goto("/indexers/download-clients");
    await expect(page.getByText(uniqueName).first()).toBeVisible();

    await page.request.delete(`/api/download-clients/${client.id}`, { headers: authHeaders() });

    await page.reload();
    await expect(page.getByText(uniqueName)).toHaveCount(0);
  });

  test("sources page provides the download connection flow", async ({ page }) => {
    await page.goto("/indexers/download-clients");
    await expect(page.getByRole("button", { name: "New client" }).first()).toBeVisible();
  });

  test("updated download client fields are returned correctly by the API", async ({ page }) => {
    const uniqueName = `Smoke-ClientUpdate-${Date.now()}`;

    const createResp = await page.request.post("/api/download-clients", {
      data: {
        name: uniqueName,
        protocol: "qbittorrent",
        host: "localhost",
        port: 8080,
        username: null,
        password: null,
        endpointUrl: null,
        moviesCategory: "deluno-movies",
        tvCategory: "deluno-tv",
        categoryTemplate: null,
        priority: 1,
        isEnabled: false
      },
      headers: authHeaders()
    });
    expect(createResp.ok()).toBe(true);
    const client = await createResp.json() as { id: string };

    try {
      // Null patch — all fields must be preserved
      const updateResp = await page.request.put(`/api/download-clients/${client.id}`, {
        data: {
          name: null,
          protocol: null,
          host: "192.168.1.50",
          port: null,
          username: null,
          password: null,
          endpointUrl: null,
          moviesCategory: null,
          tvCategory: null,
          categoryTemplate: null,
          priority: null,
          isEnabled: null
        },
        headers: authHeaders()
      });
      expect(updateResp.ok(), `PUT /api/download-clients/${client.id} failed: ${updateResp.status()}`).toBe(true);

      const updated = await updateResp.json() as { name: string; host: string; port: number };
      expect(updated.name).toBe(uniqueName);   // name preserved (null patch)
      expect(updated.host).toBe("192.168.1.50"); // host updated
      expect(updated.port).toBe(8080);           // port preserved (null patch)
    } finally {
      await page.request.delete(`/api/download-clients/${client.id}`, { headers: authHeaders() });
    }
  });

  test("editing a download client in the drawer keeps its identity", async ({ page }) => {
    const name = `Smoke-ClientDrawer-${Date.now()}`;
    const updatedHost = "after-client.example.test";
    const create = await page.request.post("/api/download-clients", {
      data: { name, protocol: "qbittorrent", host: "before-client.example.test", port: 8080, username: null, password: null, endpointUrl: null, moviesCategory: "smoke-movies", tvCategory: "smoke-tv", categoryTemplate: null, priority: 1, isEnabled: true },
      headers: authHeaders()
    });
    expect(create.ok()).toBe(true);
    const client = await create.json() as { id: string };

    try {
      await page.goto("/indexers/download-clients");
      await page.getByRole("row").filter({ hasText: name }).click();
      const drawer = page.getByRole("dialog", { name });
      await expect(drawer.getByLabel("Custom host / IP")).toHaveValue("before-client.example.test");
      await drawer.getByLabel("Custom host / IP").fill(updatedHost);
      const update = page.waitForRequest((request) => request.method() === "PUT" && new URL(request.url()).pathname === `/api/download-clients/${client.id}`);
      await drawer.getByRole("button", { name: "Save client" }).click();
      await update;
      const list = await page.request.get("/api/download-clients", { headers: authHeaders() });
      expect(list.ok()).toBe(true);
      expect((await list.json() as Array<{ id: string; host: string }>).find((item) => item.id === client.id)).toMatchObject({ id: client.id, host: updatedHost });
    } finally {
      await page.request.delete(`/api/download-clients/${client.id}`, { headers: authHeaders() });
    }
  });

  test("editing a quality profile in the drawer keeps its identity", async ({ page }) => {
    const name = `Smoke-ProfileDrawer-${Date.now()}`;
    const renamed = `${name}-renamed`;
    const qualityModel = await page.request.get("/api/quality-model", { headers: authHeaders() });
    expect(qualityModel.ok()).toBe(true);
    const tier = (await qualityModel.json() as { tiers: Array<{ name: string }> }).tiers[0]?.name;
    expect(tier).toBeTruthy();
    const create = await page.request.post("/api/quality-profiles", {
      data: { name, mediaType: "movies", cutoffQuality: tier, allowedQualities: tier, customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false },
      headers: authHeaders()
    });
    expect(create.ok()).toBe(true);
    const profile = await create.json() as { id: string };

    try {
      await page.goto("/settings/profiles");
      await page.getByRole("row").filter({ hasText: name }).click();
      const drawer = page.getByRole("dialog", { name });
      await expect(drawer.getByLabel("Profile name")).toHaveValue(name);
      await drawer.getByLabel("Profile name").fill(renamed);
      const update = page.waitForRequest((request) => request.method() === "PUT" && new URL(request.url()).pathname === `/api/quality-profiles/${profile.id}`);
      await drawer.getByRole("button", { name: "Save quality profile" }).click();
      await update;
      const list = await page.request.get("/api/quality-profiles", { headers: authHeaders() });
      expect(list.ok()).toBe(true);
      expect((await list.json() as Array<{ id: string; name: string }>).find((item) => item.id === profile.id)).toMatchObject({ id: profile.id, name: renamed });
    } finally {
      await page.request.delete(`/api/quality-profiles/${profile.id}`, { headers: authHeaders() });
    }
  });
});

test.describe("library editing", () => {
  let credentials: { username: string; password: string } | null = null;
  let authToken: string | null = null;
  const seededRoot = path.join(os.tmpdir(), "deluno-playwright-libraries");

  test.beforeAll(async ({ request }) => {
    mkdirSync(seededRoot, { recursive: true });
    const status = await request.get("/api/auth/bootstrap-status");
    const bootstrapState = status.ok() ? (await status.json()) as { requiresSetup?: boolean } : {};
    if (bootstrapState.requiresSetup) {
      const bootstrap = await request.post("/api/auth/bootstrap", { data: fallbackCredentials });
      if (bootstrap.ok()) {
        credentials = fallbackCredentials;
        return;
      }
    }
    const login = await request.post("/api/auth/login", { data: { username: fallbackCredentials.username, password: fallbackCredentials.password } });
    if (login.ok()) credentials = fallbackCredentials;
    else if (process.env.DELUNO_E2E_USERNAME && process.env.DELUNO_E2E_PASSWORD) {
      credentials = { username: process.env.DELUNO_E2E_USERNAME, password: process.env.DELUNO_E2E_PASSWORD };
    }
  });

  test.beforeEach(async ({ page }) => {
    test.skip(!credentials, "Set DELUNO_E2E_USERNAME and DELUNO_E2E_PASSWORD to run CRUD tests against an existing install.");
    await page.goto("/login");
    await page.getByLabel(/username/i).fill(credentials!.username);
    await page.getByLabel("Password", { exact: true }).fill(credentials!.password);
    await page.getByRole("button", { name: /sign in/i }).click();
    await expect(page).not.toHaveURL(/\/login/);
    authToken = await page.evaluate(() => sessionStorage.getItem("deluno-auth-token"));
  });

  function authHeaders(): Record<string, string> {
    return authToken ? { Authorization: `Bearer ${authToken}` } : {};
  }

  async function seedLibrary(page: import("@playwright/test").Page) {
    const name = `Smoke-Library-${Date.now()}`;
    const response = await page.request.post("/api/libraries", {
      headers: authHeaders(),
      data: {
        name, mediaType: "movies", purpose: "Main library", rootPath: seededRoot,
        autoSearchEnabled: false, missingSearchEnabled: true, upgradeSearchEnabled: true,
        searchIntervalHours: 12, retryDelayHours: 6, maxItemsPerRun: 10
      }
    });
    expect(response.ok(), `POST /api/libraries failed: ${response.status()}`).toBe(true);
    return { name, library: await response.json() as { id: string } };
  }

  async function libraries(page: import("@playwright/test").Page) {
    const response = await page.request.get("/api/libraries", { headers: authHeaders() });
    expect(response.ok()).toBe(true);
    return await response.json() as Array<{ id: string; name: string; rootPath: string; downloadsPath?: string | null; missingSearchEnabled: boolean; upgradeSearchEnabled: boolean }>;
  }

  test("persists the library folder without embedding search settings in the library drawer", async ({ page }) => {
    const { name, library } = await seedLibrary(page);
    const renamed = `${name}-renamed`;
    const movedRoot = path.join(seededRoot, "moved");
    mkdirSync(movedRoot, { recursive: true });
    try {
      await page.goto("/settings/libraries");
      await page.getByText(name, { exact: true }).click();
      await page.getByLabel("Library name").fill(renamed);
      await page.getByLabel("Library folder").fill(movedRoot);
      await expect(page.getByRole("switch", { name: "Find missing media" })).toHaveCount(0);
      await expect(page.getByRole("switch", { name: "Look for better releases" })).toHaveCount(0);
      await page.getByRole("button", { name: "Save library" }).click();
      await expect(page.getByText(renamed, { exact: true }).first()).toBeVisible();
      await expect.poll(async () => (await libraries(page)).find((item) => item.id === library.id)).toMatchObject({ name: renamed, rootPath: movedRoot });
    } finally {
      await page.request.delete(`/api/libraries/${library.id}`, { headers: authHeaders() });
    }
  });

  test("closes the library drawer after creating a library", async ({ page }) => {
    const name = `Smoke-UI-Library-${Date.now()}`;
    const rootPath = path.join(seededRoot, "ui-created");
    mkdirSync(rootPath, { recursive: true });
    let createdId: string | undefined;

    try {
      await page.goto("/settings/libraries");
      await page.getByRole("button", { name: "New library", exact: true }).first().click();
      const drawer = page.getByRole("dialog", { name: "New library" });
      await drawer.getByLabel("Library name").fill(name);
      await drawer.getByLabel("Library folder").fill(rootPath);
      await drawer.getByRole("button", { name: "Create library", exact: true }).click();

      await expect(drawer).toBeHidden();
      await expect(page.getByText(name, { exact: true }).first()).toBeVisible();
      createdId = (await libraries(page)).find((item) => item.name === name)?.id;
      expect(createdId).toBeDefined();
    } finally {
      if (createdId) await page.request.delete(`/api/libraries/${createdId}`, { headers: authHeaders() });
    }
  });

  test("removes a library only after the explicit confirmation", async ({ page }) => {
    const { name, library } = await seedLibrary(page);
    await page.goto("/settings/libraries");
    await page.getByText(name, { exact: true }).click();
    await page.getByRole("button", { name: /remove/i }).click();
    await page.getByRole("button", { name: "Remove library" }).click();
    await expect(page.getByText(name, { exact: true })).toHaveCount(0);
    await expect.poll(async () => (await libraries(page)).some((item) => item.id === library.id)).toBe(false);
  });

  test("asks before leaving an edited library", async ({ page }) => {
    test.setTimeout(60_000);
    const { name, library } = await seedLibrary(page);
    try {
      await page.goto("/settings/libraries");
      await page.getByText(name, { exact: true }).click();
      await page.getByLabel("Library name").fill(`${name}-draft`);
      await page.locator('a[href="/settings/general"]').first().evaluate((link: HTMLAnchorElement) => link.click());
      const dialog = page.getByRole("dialog", { name: "Unsaved changes" });
      await expect(dialog).toBeVisible();
      await dialog.getByRole("button", { name: "Discard and continue", exact: true }).click();
      await expect(page).toHaveURL(/\/settings\/general$/);
    } finally {
      await page.request.delete(`/api/libraries/${library.id}`, { headers: authHeaders() });
    }
  });
});
