import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1082: the Dashboard is four tabs (Overview, Library, Downloads, System).
// Every tab fits one screen at 1920x1080 and 2560x1440: the page never
// scrolls, whatever the tab holds, and a tab can be linked with ?tab=.

const TABS = [
  { name: "Overview", query: "", panel: /^needs you$/i },
  { name: "Library", query: "?tab=library", panel: /^wanted$/i },
  { name: "Downloads", query: "?tab=downloads", panel: null },
  { name: "System", query: "?tab=system", panel: null }
] as const;

const SIZES = [
  { width: 1920, height: 1080 },
  { width: 2560, height: 1440 }
] as const;

async function fit(page: Page) {
  return page.evaluate(() => ({
    scrollHeight: document.documentElement.scrollHeight,
    innerHeight: window.innerHeight,
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: window.innerWidth
  }));
}

async function expectFitsOneScreen(page: Page, label: string) {
  // Let the tab's own lists arrive: a late list must not push the page down.
  await page.waitForTimeout(900);
  const measured = await fit(page);
  console.log(`${label}: scrollHeight ${measured.scrollHeight} / innerHeight ${measured.innerHeight}`);
  expect(measured.scrollHeight, `${label} scrolls the page`).toBeLessThanOrEqual(measured.innerHeight + 1);
  // The tab really fills the space under the header (a collapsed panel would pass the check above trivially).
  const box = await page.getByRole("tabpanel").boundingBox();
  expect(box?.height ?? 0, `${label} panel is collapsed`).toBeGreaterThan(400);
  expect((box?.y ?? 0) + (box?.height ?? 0), `${label} panel runs past the window`).toBeLessThanOrEqual(measured.innerHeight + 1);
  expect(measured.scrollWidth, `${label} scrolls sideways`).toBeLessThanOrEqual(measured.innerWidth + 1);
}

test.describe("Dashboard tabs", () => {
  test.skip(({ isMobile }) => isMobile, "The one-screen rule is for a desktop window; a phone scrolls.");

  test("opens on the Overview and deep links open each tab", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await expect(page.getByRole("tab", { name: /^Overview/ })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("region", { name: /^needs you$/i })).toBeVisible();

    for (const tab of TABS.slice(1)) {
      await page.goto(`/${tab.query}`);
      await expect(page.getByRole("tab", { name: new RegExp(`^${tab.name}`) })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByRole("tabpanel")).toBeVisible();
    }
  });

  test("switching tabs changes the address without a reload and keeps the page", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await authenticateAndNavigate(page, "/");
    await expect(page.getByRole("tab", { name: /^Overview/ })).toBeVisible();
    await page.evaluate(() => { (window as unknown as { __kept: boolean }).__kept = true; });

    await page.getByRole("tab", { name: /^Library/ }).click();
    await expect(page).toHaveURL(/\/\?tab=library$/);
    await page.getByRole("tab", { name: /^System/ }).click();
    await expect(page).toHaveURL(/\/\?tab=system$/);
    await page.getByRole("tab", { name: /^Overview/ }).click();
    await expect(page).toHaveURL(/\/$/);

    expect(await page.evaluate(() => (window as unknown as { __kept?: boolean }).__kept)).toBe(true);
  });

  test("a fresh install fits every tab on one screen at both sizes", async ({ page }) => {
    await authenticateAndNavigate(page, "/");
    for (const size of SIZES) {
      await page.setViewportSize(size);
      for (const tab of TABS) {
        await page.goto(`/${tab.query}`);
        await expect(page.getByRole("tab", { name: new RegExp(`^${tab.name}`) })).toHaveAttribute("aria-selected", "true");
        await expectFitsOneScreen(page, `fresh ${tab.name} ${size.width}x${size.height}`);
      }
    }
  });

  test.describe("with a library to look at", () => {
    let token = "";

    test.beforeAll(async ({ request }) => {
      const login = await request.post("/api/auth/login", {
        data: { username: process.env.DELUNO_E2E_USERNAME ?? "e2e-test-user", password: process.env.DELUNO_E2E_PASSWORD ?? "e2e-test-password-123" }
      });
      if (login.ok()) token = ((await login.json()) as { accessToken: string }).accessToken;
    });

    test("every tab still fits one screen at both sizes", async ({ page, request }) => {
      test.setTimeout(120_000);
      await authenticateAndNavigate(page, "/");
      token = (await page.evaluate(() => localStorage.getItem("deluno-auth-token"))) ?? token;
      const headers = { Authorization: `Bearer ${token}` };
      const seeded = await request.post("/api/testing/movies/seed-large-library", { headers, data: { count: 300 }, timeout: 60_000 });
      expect(seeded.ok(), `Seeding failed: HTTP ${seeded.status()}`).toBe(true);
      try {
        for (const size of SIZES) {
          await page.setViewportSize(size);
          for (const tab of TABS) {
            await page.goto(`/${tab.query}`);
            await expect(page.getByRole("tab", { name: new RegExp(`^${tab.name}`) })).toHaveAttribute("aria-selected", "true");
            if (tab.panel) await expect(page.getByRole("region", { name: tab.panel })).toBeVisible();
            await expectFitsOneScreen(page, `seeded ${tab.name} ${size.width}x${size.height}`);
          }
        }
      } finally {
        await request.post("/api/testing/movies/unseed-large-library", { headers, timeout: 60_000 });
      }
    });
  });
});
