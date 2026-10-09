/**
 * CRUD flow smoke tests.
 *
 * These tests use page.request (authenticated via Bearer token extracted from
 * localStorage after browser login) to set up data via the API, then verify
 * the UI renders and responds correctly.
 * This is more reliable than testing form submission directly, which is brittle
 * against label/placeholder changes.
 */
import { expect, test } from "@playwright/test";
import { mkdirSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { authenticateAndNavigate, fallbackCredentials } from "../helpers/auth-helper";

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

    // The app stores auth in localStorage (not cookies), so page.request calls
    // need an explicit Authorization header. Extract the token here for test use.
    authToken = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  });

  function authHeaders(): Record<string, string> {
    return authToken ? { Authorization: `Bearer ${authToken}` } : {};
  }

  // ── Indexer CRUD ──────────────────────────────────────────────────────────

  test("indexer created via API appears on the indexers page", async ({ page }) => {
    // #1076: an indexer is named for its site, never typed.
    const uniqueName = `smoke-indexer-${Date.now()}.example.test`;

    const createResp = await page.request.post("/api/indexers", {
      data: {
        name: uniqueName,
        protocol: "torznab",
        privacy: "private",
        baseUrl: `https://${uniqueName}`,
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
      await expect(page.locator("#main-content").getByText(uniqueName).first()).toBeVisible();
    } finally {
      // Cleanup
      await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    }
  });

  test("indexer deleted via API disappears from the indexers page", async ({ page }) => {
    const uniqueName = `smoke-del-${Date.now()}.example.test`;

    const createResp = await page.request.post("/api/indexers", {
      data: {
        name: uniqueName,
        protocol: "rss",
        privacy: "public",
        baseUrl: `https://${uniqueName}`,
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
    await expect(page.locator("#main-content").getByText(uniqueName).first()).toBeVisible();

    // Delete via API
    const deleteResp = await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    expect(deleteResp.ok()).toBe(true);

    // Reload and verify gone
    await page.reload();
    await expect(page.locator("#main-content").getByText(uniqueName)).toHaveCount(0);
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

  // #626: a new indexer can be tested before it is saved, and the result is
  // shown in the drawer next to the form it tested.
  test("a new indexer is tested with what the form holds, before saving", async ({ page }) => {
    let sent: Record<string, unknown> | null = null;
    await page.route("**/api/indexers/test", async (route) => {
      sent = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({ json: { healthStatus: "healthy", message: "Reached api.example.test and received a valid Newznab response.", latencyMs: 12 } });
    });

    await page.goto("/indexers/indexers");
    await page.getByRole("button", { name: "New indexer" }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Protocol").selectOption("newznab");
    await drawer.getByLabel("Newznab URL").fill("https://api.example.test");
    await drawer.getByLabel("API key").fill("draft-key");

    await expect(drawer.getByRole("button", { name: "Test", exact: true })).toHaveCount(1);
    await drawer.getByRole("button", { name: "Test", exact: true }).click();

    await expect(drawer.getByText("Reached api.example.test and received a valid Newznab response.")).toBeVisible();
    expect(sent).toMatchObject({ protocol: "newznab", baseUrl: "https://api.example.test", apiKey: "draft-key" });
    await page.keyboard.press("Escape");
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
    const uniqueName = `smoke-update-${Date.now()}.example.test`;

    const createResp = await page.request.post("/api/indexers", {
      data: {
        name: uniqueName,
        protocol: "rss",
        privacy: "public",
        baseUrl: `https://${uniqueName}`,
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
      // Update only the address (the name follows it, #1076) — mediaScope must be preserved (regression for the newScope PATCH bug)
      const updateResp = await page.request.put(`/api/indexers/${indexer.id}`, {
        data: {
          name: null,
          protocol: null,
          privacy: null,
          baseUrl: `https://renamed-${uniqueName}`,
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
      expect(updated.name).toBe(`renamed-${uniqueName}`);
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
    const name = `smoke-indexerdrawer-${Date.now()}.example.test`;
    const updatedUrl = "https://after-indexer.example.test/feed.rss";
    const create = await page.request.post("/api/indexers", {
      data: { name, protocol: "rss", privacy: "public", baseUrl: `https://${name}/feed.rss`, priority: 10, categories: "", tags: "", mediaScope: "movies", isEnabled: true },
      headers: authHeaders()
    });
    expect(create.ok()).toBe(true);
    const indexer = await create.json() as { id: string };

    try {
      await page.goto("/indexers/indexers");
      await page.getByRole("row").filter({ hasText: name }).click();
      const drawer = page.getByRole("dialog", { name });
      await expect(drawer.getByLabel("URL")).toHaveValue(`https://${name}/feed.rss`);
      await drawer.getByLabel("URL").fill(updatedUrl);
      const update = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/indexers/${indexer.id}`);
      await drawer.getByRole("button", { name: "Save indexer" }).click();
      expect((await update).ok()).toBe(true);
      const list = await page.request.get("/api/indexers", { headers: authHeaders() });
      expect(list.ok()).toBe(true);
      expect((await list.json() as Array<{ id: string; baseUrl: string }>).find((item) => item.id === indexer.id)).toMatchObject({ id: indexer.id, baseUrl: updatedUrl });
    } finally {
      await page.request.delete(`/api/indexers/${indexer.id}`, { headers: authHeaders() });
    }
  });

  test("shows when Deluno is proactively pacing an indexer", async ({ page }, testInfo) => {
    const host = `smoke-pacing-${Date.now()}.example.test`;
    const uniqueName = host;
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
    // #1076: a download client is named "app on host", never typed.
    const host = `smoke-client-${Date.now()}.example.test`;
    const uniqueName = `qBittorrent on ${host}`;

    const createResp = await page.request.post("/api/download-clients", {
      data: {
        name: uniqueName,
        protocol: "qbittorrent",
        host,
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
      await expect(page.locator("#main-content").getByText(uniqueName).first()).toBeVisible();
    } finally {
      await page.request.delete(`/api/download-clients/${client.id}`, { headers: authHeaders() });
    }
  });

  test("download client deleted via API disappears from the indexers page", async ({ page }) => {
    const host = `smoke-clientdel-${Date.now()}.example.test`;
    const uniqueName = `Transmission on ${host}`;

    const createResp = await page.request.post("/api/download-clients", {
      data: {
        name: uniqueName,
        protocol: "transmission",
        host,
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
    await expect(page.locator("#main-content").getByText(uniqueName).first()).toBeVisible();

    await page.request.delete(`/api/download-clients/${client.id}`, { headers: authHeaders() });

    await page.reload();
    await expect(page.locator("#main-content").getByText(uniqueName)).toHaveCount(0);
  });

  test("sources page provides the download connection flow", async ({ page }) => {
    await page.goto("/indexers/download-clients");
    await expect(page.getByRole("button", { name: "New client" }).first()).toBeVisible();
  });

  test("updated download client fields are returned correctly by the API", async ({ page }) => {
    const host = `smoke-clientupdate-${Date.now()}.example.test`;
    const uniqueName = `qBittorrent on ${host}`;

    const createResp = await page.request.post("/api/download-clients", {
      data: {
        name: uniqueName,
        protocol: "qbittorrent",
        host,
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
      expect(updated.name).toBe("qBittorrent on 192.168.1.50"); // the name follows the host (#1076)
      expect(updated.host).toBe("192.168.1.50"); // host updated
      expect(updated.port).toBe(8080);           // port preserved (null patch)
    } finally {
      await page.request.delete(`/api/download-clients/${client.id}`, { headers: authHeaders() });
    }
  });

  test("editing a download client in the drawer keeps its identity", async ({ page }) => {
    const name = `qBittorrent on before-${Date.now()}.example.test`;
    const updatedHost = "after-client.example.test";
    const create = await page.request.post("/api/download-clients", {
      data: { name, protocol: "qbittorrent", host: name.replace("qBittorrent on ", ""), port: 8080, username: null, password: null, endpointUrl: null, moviesCategory: "smoke-movies", tvCategory: "smoke-tv", categoryTemplate: null, priority: 1, isEnabled: true },
      headers: authHeaders()
    });
    expect(create.ok()).toBe(true);
    const client = await create.json() as { id: string };

    try {
      await page.goto("/indexers/download-clients");
      await page.getByRole("row").filter({ hasText: name }).click();
      const drawer = page.getByRole("dialog", { name });
      await expect(drawer.getByLabel("Custom host / IP")).toHaveValue(name.replace("qBittorrent on ", ""));
      await drawer.getByLabel("Custom host / IP").fill(updatedHost);
      const update = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/download-clients/${client.id}`);
      await drawer.getByRole("button", { name: "Save client" }).click();
      expect((await update).ok()).toBe(true);
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
      const update = page.waitForResponse((response) => response.request().method() === "PUT" && new URL(response.url()).pathname === `/api/quality-profiles/${profile.id}`);
      await drawer.getByRole("button", { name: "Save quality profile" }).click();
      expect((await update).ok()).toBe(true);
      const list = await page.request.get("/api/quality-profiles", { headers: authHeaders() });
      expect(list.ok()).toBe(true);
      expect((await list.json() as Array<{ id: string; name: string }>).find((item) => item.id === profile.id)).toMatchObject({ id: profile.id, name: renamed });
    } finally {
      await page.request.delete(`/api/quality-profiles/${profile.id}`, { headers: authHeaders() });
    }
  });

  test("a profile traces every preference back to the legacy rule and value behind it", async ({ page }) => {
    // #351: the owner can trace every new preference back to the exact legacy
    // rule and value that produced it. The drawer used to name the retained
    // rule and stop there, which is a claim about provenance rather than the
    // provenance itself.
    const stamp = Date.now();
    const formatName = `Smoke-Trace-Format-${stamp}`;
    const profileName = `Smoke-Trace-Profile-${stamp}`;
    const trashId = `smoke-trace-${stamp}`;
    // #745 taught CustomFormatDefinitionMatcher to read IndexerFlagSpecification
    // as a real typed condition (ExactTyped), so it stopped being an example of
    // a legacy rule with no safe typed translation - the same fixture drift
    // MigrationAssistantServiceTests hit and fixed in the same way (Refs #745).
    // SizeSpecification has no typed mapping, so it stays genuinely unmapped
    // and still exercises the "kept as-is" advanced-review path this test names.
    const conditions = JSON.stringify([{
      name: "Size",
      implementation: "SizeSpecification",
      fields: [{ name: "value", value: "20000" }],
      negate: false,
      required: true
    }]);
    const legacyScore = 4321;

    const qualityModel = await page.request.get("/api/quality-model", { headers: authHeaders() });
    expect(qualityModel.ok()).toBe(true);
    const tier = (await qualityModel.json() as { tiers: Array<{ name: string }> }).tiers[0]?.name;
    expect(tier).toBeTruthy();

    const createFormat = await page.request.post("/api/custom-formats", {
      data: { name: formatName, mediaType: "movies", score: legacyScore, trashId, conditions, upgradeAllowed: true },
      headers: authHeaders()
    });
    expect(createFormat.ok()).toBe(true);
    const format = await createFormat.json() as { id: string };

    // A second format that the guide has actually reviewed, so the plan gains
    // a typed preference with a source. Without one the only thing to trace is
    // the retained rule, and the plan-provenance half is never exercised.
    const mappedScore = 777;
    const createMapped = await page.request.post("/api/custom-formats", {
      data: {
        name: `Smoke-Trace-HDR10-${stamp}`,
        mediaType: "movies",
        score: mappedScore,
        trashId: "deluno-hdr-hdr10",
        conditions: "\bHDR10\b",
        upgradeAllowed: true
      },
      headers: authHeaders()
    });
    expect(createMapped.ok()).toBe(true);
    const mapped = await createMapped.json() as { id: string };

    const createProfile = await page.request.post("/api/quality-profiles", {
      data: {
        name: profileName,
        mediaType: "movies",
        cutoffQuality: tier,
        allowedQualities: tier,
        customFormatIds: `${format.id},${mapped.id}`,
        upgradeUntilCutoff: true,
        upgradeUnknownItems: false,
        // The profile's own answers. A rule nobody has turned on is off,
        // whatever the guide scores it (#550), so the answer this test reads
        // back has to have been given.
        formatIntents: { [format.id]: "strong-prefer", [mapped.id]: "strong-prefer" }
      },
      headers: authHeaders()
    });
    expect(createProfile.ok()).toBe(true);
    const profile = await createProfile.json() as { id: string };

    try {
      await page.goto("/settings/profiles");
      await page.getByRole("row").filter({ hasText: profileName }).click();
      const drawer = page.getByRole("dialog", { name: profileName });

      await drawer.getByRole("button", { name: /Advanced review/ }).click();
      // The rule, the value it carried, its matcher, and what Deluno decided
      // to do with it - each of which the owner would otherwise have to take
      // on trust.
      await expect(drawer.getByText(String(legacyScore))).toBeVisible();
      await expect(drawer.getByText(trashId, { exact: false }).first()).toBeVisible();
      await expect(drawer.getByText(conditions, { exact: false }).first()).toBeVisible();
      await expect(drawer.getByText("Kept as-is; no safe typed translation yet")).toBeVisible();

      // And the reviewed one became a typed preference, so the plan can say
      // which rule and which value produced it.
      await drawer.getByRole("button", { name: /Where these came from/ }).click();
      const provenance = drawer.getByLabel("Preference provenance");
      await expect(provenance).toBeVisible();
      await expect(provenance.getByText("deluno-hdr-hdr10")).toBeVisible();
      await expect(provenance.getByText(`510 → ${mappedScore} in this profile`)).toBeVisible();
      await expect(provenance.getByText("Strongly prefer")).toBeVisible();
      await expect(provenance.getByText(/Hdr10/i).first()).toBeVisible();
      await expect(provenance.getByText("deluno-custom-format/v1")).toBeVisible();
    } finally {
      await page.request.delete(`/api/quality-profiles/${profile.id}`, { headers: authHeaders() });
      await page.request.delete(`/api/custom-formats/${format.id}`, { headers: authHeaders() });
      await page.request.delete(`/api/custom-formats/${mapped.id}`, { headers: authHeaders() });
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
    // Cause of the flake (#1191): signing in through the login form paid a cold render of /login (its loader waits on
    // bootstrap-status) before every test, and on a busy machine that wait plus the test's own steps outran the 30 s budget,
    // so the delete in the finally block was the call that happened to be waiting when time ran out, and the library stayed
    // behind for every later spec. These tests are about libraries, not the form: sign in through the API as the others do,
    // and give the budget room for the drawer steps under load.
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/");
    authToken = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
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
        searchIntervalMinutes: 12, retryDelayHours: 6, maxItemsPerRun: 10
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

  // #1079: a library's downloads folder could only be set through the API.
  test("a library's downloads folder is set from the library drawer and kept when only the name changes", async ({ page }) => {
    const { name, library } = await seedLibrary(page);
    const downloads = path.join(seededRoot, "downloads");
    mkdirSync(downloads, { recursive: true });
    try {
      await page.goto("/settings/libraries");
      await page.getByText(name, { exact: true }).click();
      await page.getByLabel("Downloads folder").fill(downloads);
      await page.getByRole("button", { name: "Save library" }).click();
      await expect.poll(async () => (await libraries(page)).find((item) => item.id === library.id)?.downloadsPath).toBe(downloads);
      await expect(page.getByRole("list", { name: "Folder check" })).toBeVisible();

      await page.getByLabel("Library name").fill(`${name}-b`);
      await page.getByRole("button", { name: "Save library" }).click();
      await expect.poll(async () => (await libraries(page)).find((item) => item.id === library.id)).toMatchObject({ name: `${name}-b`, downloadsPath: downloads });
    } finally {
      await page.request.delete(`/api/libraries/${library.id}`, { headers: authHeaders() });
    }
  });

  // #589: the link went to /settings/automation, whose redirect dropped the
  // library it named, so it opened the whole Automation page instead.
  test("a library's link to its searches opens that library's schedule", async ({ page }, testInfo) => {
    // The Libraries table keeps its schedule column for wider screens.
    test.skip(testInfo.project.name === "mobile", "The schedule column is not shown on a phone.");
    const { name, library } = await seedLibrary(page);
    try {
      await page.goto("/settings/libraries");
      await page.getByRole("row").filter({ hasText: name }).getByRole("link", { name: /^(Manage|Configure searches)$/ }).first().click();
      await expect(page.getByRole("dialog", { name })).toBeVisible();
    } finally {
      await page.request.delete(`/api/libraries/${library.id}`, { headers: authHeaders() });
    }
  });

  test("the old automation address still opens the library it names", async ({ page }) => {
    const { name, library } = await seedLibrary(page);
    try {
      await page.goto(`/settings/automation?libraryId=${library.id}`);
      await expect(page.getByRole("dialog", { name })).toBeVisible();
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
