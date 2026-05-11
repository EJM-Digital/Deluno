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

const fallbackCredentials = {
  displayName: "Deluno Smoke User",
  username: "deluno-smoke",
  password: "deluno-smoke-password"
};

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
      await page.goto("/indexers");
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
    await page.goto("/indexers");
    await expect(page.getByText(uniqueName).first()).toBeVisible();

    // Delete via API
    const deleteResp = await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    expect(deleteResp.ok()).toBe(true);

    // Reload and verify gone
    await page.reload();
    await expect(page.getByText(uniqueName)).toHaveCount(0);
  });

  test("indexers page shows Add indexer button and opens the add form", async ({ page }) => {
    await page.goto("/indexers");
    const addButton = page.getByRole("button", { name: "Add indexer" });
    await expect(addButton).toBeVisible();

    await addButton.click();
    // Form heading appears
    await expect(page.getByText("Add indexer").first()).toBeVisible();
    // Protocol options are visible
    await expect(page.getByRole("button", { name: /Torznab/i })).toBeVisible();
    await expect(page.getByRole("button", { name: /Newznab/i })).toBeVisible();
    await expect(page.getByRole("button", { name: /RSS Feed/i })).toBeVisible();
  });

  test("indexer add form shows URL and scope fields after selecting protocol", async ({ page }) => {
    await page.goto("/indexers");
    await page.getByRole("button", { name: "Add indexer" }).click();
    await page.getByRole("button", { name: /Torznab/i }).click();

    // After selecting protocol, media scope and URL field should be visible
    await expect(page.getByText(/Movies \+ TV/i)).toBeVisible();
    await expect(page.getByPlaceholder(/localhost:9117/i)).toBeVisible();
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
      await page.goto("/indexers");
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

    await page.goto("/indexers");
    await expect(page.getByText(uniqueName).first()).toBeVisible();

    await page.request.delete(`/api/download-clients/${client.id}`, { headers: authHeaders() });

    await page.reload();
    await expect(page.getByText(uniqueName)).toHaveCount(0);
  });

  test("download clients page shows Add client button", async ({ page }) => {
    await page.goto("/indexers");
    await expect(page.getByRole("button", { name: /Add client/i })).toBeVisible();
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
