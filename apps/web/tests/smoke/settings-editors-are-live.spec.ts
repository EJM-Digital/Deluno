import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1179: a Settings screen left open shows a change saved from anywhere else (another tab, an import,
// an automation) with no reload and no timer. Each test opens the screen, makes the change over the
// API as another tab would, and checks the screen takes it. The server announces each write itself
// (the area-changed push, or the library and settings pushes), so these prove the server side too.
// Everything a test creates is removed again: later specs in the shared database assume it is empty.

async function token(page: Page) {
  return page.evaluate(() => localStorage.getItem("deluno-auth-token"));
}

function countReloads(page: Page) {
  let navigations = 0;
  page.on("framenavigated", (frame) => { if (frame === page.mainFrame()) navigations += 1; });
  return () => navigations;
}

async function watchingDashboard(page: Page) {
  await expect.poll(async () => page.evaluate(async () => {
    const t = localStorage.getItem("deluno-auth-token");
    const response = await fetch("/api/testing/realtime/watchers", { headers: { Authorization: `Bearer ${t}` } });
    return ((await response.json()) as { dashboard: number }).dashboard;
  }), { timeout: 15_000, message: "the page never joined the dashboard subject" }).toBeGreaterThan(0);
}

/** Opens a Settings screen and waits until it is listening, so the change made next cannot be missed. */
async function open(page: Page, route: string) {
  await authenticateAndNavigate(page, "/");
  const navigations = countReloads(page);
  await page.goto(route);
  return navigations;
}

/** Makes a change over the API, checks the screen shows it with no reload, then removes it. */
async function createdElsewhere(
  page: Page,
  route: string,
  create: (headers: Record<string, string>) => Promise<{ name: string; remove: () => Promise<unknown> }>,
  ready: () => Promise<void>,
  isMobile: boolean
) {
  const navigations = await open(page, route);
  await ready();
  const afterLoad = navigations();
  await watchingDashboard(page);

  const headers = { Authorization: `Bearer ${await token(page)}` };
  const made = await create(headers);
  try {
    const shown = page.getByText(made.name).first();
    await (isMobile ? expect(shown).toBeAttached({ timeout: 15_000 }) : expect(shown).toBeVisible({ timeout: 15_000 }));
    expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
  } finally {
    await made.remove();
  }
}

test.describe("Settings editors follow changes made elsewhere (#1179)", () => {
  test("Libraries shows a library added elsewhere", async ({ page, request, isMobile }) => {
    test.setTimeout(90_000);
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-live-library-"));
    const name = `Live library ${Date.now()}`;
    try {
      await createdElsewhere(page, "/settings/libraries",
        async (headers) => {
          const created = await request.post("/api/libraries", { headers, data: { name, mediaType: "movies", purpose: "", rootPath: folder, autoSearchEnabled: false } });
          expect(created.ok(), `POST /api/libraries failed: ${created.status()}`).toBe(true);
          const id = ((await created.json()) as { id: string }).id;
          return { name, remove: () => request.delete(`/api/libraries/${id}`, { headers }) };
        },
        () => expect(page.getByRole("heading", { name: /librar/i }).first()).toBeVisible(),
        isMobile);
    } finally {
      fs.rmSync(folder, { recursive: true, force: true });
    }
  });

  test("Quality profiles shows a profile added elsewhere", async ({ page, request, isMobile }) => {
    test.setTimeout(90_000);
    const name = `Live profile ${Date.now()}`;
    await createdElsewhere(page, "/settings/profiles",
      async (headers) => {
        const created = await request.post("/api/quality-profiles", {
          headers,
          data: { name, mediaType: "movies", cutoffQuality: "Bluray 1080p", allowedQualities: "WEB 1080p,Bluray 1080p", customFormatIds: "", upgradeUntilCutoff: false, upgradeUnknownItems: false }
        });
        expect(created.ok(), `POST /api/quality-profiles failed: ${created.status()} ${await created.text()}`).toBe(true);
        const id = ((await created.json()) as { id: string }).id;
        return { name, remove: () => request.delete(`/api/quality-profiles/${id}`, { headers }) };
      },
      () => expect(page.getByRole("heading").first()).toBeVisible(),
      isMobile);
  });

  test("Custom formats shows a format added elsewhere", async ({ page, request, isMobile }) => {
    test.setTimeout(90_000);
    const name = `Live format ${Date.now()}`;
    await createdElsewhere(page, "/settings/custom-formats",
      async (headers) => {
        const created = await request.post("/api/custom-formats", { headers, data: { name, mediaType: "movies", score: 10, conditions: "", upgradeAllowed: true } });
        expect(created.ok(), `POST /api/custom-formats failed: ${created.status()}`).toBe(true);
        const id = ((await created.json()) as { id: string }).id;
        return { name, remove: () => request.delete(`/api/custom-formats/${id}`, { headers }) };
      },
      () => expect(page.getByRole("heading").first()).toBeVisible(),
      isMobile);
  });

  test("Notifications shows a webhook added elsewhere", async ({ page, request, isMobile }) => {
    test.setTimeout(90_000);
    const host = `live-${Date.now()}.example.com`;
    await createdElsewhere(page, "/settings/notifications",
      async (headers) => {
        const created = await request.post("/api/notification-webhooks", { headers, data: { name: host, url: `https://${host}/live`, eventFilters: "", isEnabled: false } });
        expect(created.ok(), `POST /api/notification-webhooks failed: ${created.status()}`).toBe(true);
        // Deluno names a webhook after its address, so the screen shows the name the server gave it.
        const body = (await created.json()) as { id: string; name: string };
        return { name: body.name, remove: () => request.delete(`/api/notification-webhooks/${body.id}`, { headers }) };
      },
      () => expect(page.getByRole("heading").first()).toBeVisible(),
      isMobile);
  });

  test("Media servers shows a server added elsewhere", async ({ page, request, isMobile }) => {
    test.setTimeout(90_000);
    const name = `Live media server ${Date.now()}`;
    await createdElsewhere(page, "/indexers/media-servers",
      async (headers) => {
        const created = await request.post("/api/media-servers", { headers, data: { kind: "emby", name, baseUrl: "http://192.168.1.50:8096", token: "probe", isEnabled: false } });
        expect(created.ok(), `POST /api/media-servers failed: ${created.status()}`).toBe(true);
        const body = (await created.json()) as { id: string; name: string };
        return { name: body.name, remove: () => request.delete(`/api/media-servers/${body.id}`, { headers }) };
      },
      () => expect(page.getByRole("heading").first()).toBeVisible(),
      isMobile);
  });

  test("General follows a setting saved elsewhere into the open form", async ({ page, request }) => {
    test.setTimeout(90_000);
    const navigations = await open(page, "/settings/general");
    const helper = page.getByLabel("Helper address");
    await expect(helper).toBeVisible();
    const original = await helper.inputValue();
    const afterLoad = navigations();
    await watchingDashboard(page);

    const headers = { Authorization: `Bearer ${await token(page)}` };
    const changed = original === "http://127.0.0.1:8191" ? "http://127.0.0.1:8192" : "http://127.0.0.1:8191";
    try {
      const saved = await request.patch("/api/settings", { headers, data: { cloudflareHelperUrl: changed } });
      expect(saved.ok(), `PATCH /api/settings failed: ${saved.status()} ${await saved.text()}`).toBe(true);
      await expect(helper).toHaveValue(changed, { timeout: 15_000 });
      expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
    } finally {
      await request.patch("/api/settings", { headers, data: { cloudflareHelperUrl: original } });
    }
  });

  test("Subtitles follows a setting saved elsewhere, and a library added elsewhere", async ({ page, request, isMobile }) => {
    test.setTimeout(90_000);
    const navigations = await open(page, "/settings/subtitles");
    const automatic = page.getByRole("switch", { name: "Fetch subtitles automatically" });
    await expect(automatic).toBeVisible();
    const wasOn = (await automatic.getAttribute("aria-checked")) === "true";
    const afterLoad = navigations();
    await watchingDashboard(page);

    const headers = { Authorization: `Bearer ${await token(page)}` };
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "deluno-live-subtitles-"));
    let libraryId: string | null = null;
    try {
      const saved = await request.patch("/api/settings", { headers, data: { subtitleFetchAutomaticEnabled: !wasOn } });
      expect(saved.ok(), `PATCH /api/settings failed: ${saved.status()} ${await saved.text()}`).toBe(true);
      await expect(automatic).toHaveAttribute("aria-checked", String(!wasOn), { timeout: 15_000 });

      const name = `Live subtitle library ${Date.now()}`;
      const created = await request.post("/api/libraries", { headers, data: { name, mediaType: "movies", purpose: "", rootPath: folder, autoSearchEnabled: false } });
      expect(created.ok(), `POST /api/libraries failed: ${created.status()}`).toBe(true);
      libraryId = ((await created.json()) as { id: string }).id;
      const shown = page.getByText(name).first();
      await (isMobile ? expect(shown).toBeAttached({ timeout: 15_000 }) : expect(shown).toBeVisible({ timeout: 15_000 }));
      expect(navigations(), "the page reloaded or navigated").toBe(afterLoad);
    } finally {
      await request.patch("/api/settings", { headers, data: { subtitleFetchAutomaticEnabled: wasOn } });
      if (libraryId) await request.delete(`/api/libraries/${libraryId}`, { headers });
      fs.rmSync(folder, { recursive: true, force: true });
    }
  });
});
