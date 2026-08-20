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
});
