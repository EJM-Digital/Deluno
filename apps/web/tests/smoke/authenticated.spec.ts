import { expect, test } from "@playwright/test";
import { fallbackCredentials } from "../helpers/auth-helper";

let credentials: { username: string; password: string } | null = null;

test.describe("authenticated app smoke", () => {
  test.beforeAll(async ({ request }) => {
    const statusResponse = await request.get("/api/auth/bootstrap-status");
    const status = statusResponse.ok() ? ((await statusResponse.json()) as { requiresSetup?: boolean }) : { requiresSetup: false };

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
      data: {
        username: fallbackCredentials.username,
        password: fallbackCredentials.password
      }
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
    test.skip(!credentials, "Existing install detected. Set DELUNO_E2E_USERNAME and DELUNO_E2E_PASSWORD to run authenticated route checks against it.");

    await page.goto("/login");
    await page.getByLabel(/username/i).fill(credentials!.username);
    await page.getByLabel("Password", { exact: true }).fill(credentials!.password);
    await page.getByRole("button", { name: /sign in/i }).click();
    await expect(page).not.toHaveURL(/\/login/);
  });

  test("keeps the browser fetch implementation native after sign-in", async ({ page }) => {
    await expect
      .poll(() => page.evaluate(() => window.fetch.toString()))
      .toContain("[native code]");
  });

  for (const path of [
    "/",
    "/movies",
    "/movies/upgrades",
    "/tv",
    "/tv/upgrades",
    "/collections",
    "/calendar",
    "/indexers",
    "/indexers/indexers",
    "/indexers/download-clients",
    "/indexers/library-routing",
    "/activity",
    "/queue",
    "/settings",
    "/settings/media-management",
    "/settings/processing",
    "/settings/destination-rules",
    "/settings/profiles",
    "/settings/quality",
    "/settings/custom-formats",
    "/settings/release-rules",
    "/settings/lists",
    "/settings/automation",
    "/settings/metadata",
    "/settings/general",
    "/settings/notifications",
    "/settings/migration",
    "/settings/ui",
    "/system",
    "/system/api",
    "/system/docs"
  ]) {
    test(`loads ${path} with a signed-in user`, async ({ page }) => {
      await page.goto(path);
      // The page has to have drawn before "no error" means anything, and the
      // first paint of a lazily loaded route is what a busy machine slows
      // down (#1057), so wait for the content first and only then look for
      // an error, instead of checking for absence against a blank page.
      await expect(page.locator("body")).toContainText(/Deluno|Overview|Movies|Settings|System|Calendar|Activity|Indexers|API/, { timeout: 15_000 });
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      await expect(page.getByText("This area could not load")).toHaveCount(0);
    });
  }

  // #1086: an activity event is named in plain words on every screen that
  // lists one. An internal kind such as `download.vanished.recovered` or
  // `processing.waiting` must never show as text.
  const rawEventCode =
    /\b(job|jobs|download|downloads|processing|processor|filesystem|library|metadata|retention|intake|release|health|tags|trackers|subtitle|connectivity|seerr|movie|series|episode|external|collection|quality|activity)\.[a-z][a-z0-9_-]*(\.[a-z][a-z0-9_-]*)*\b/;

  for (const path of ["/", "/activity", "/system"]) {
    test(`shows no internal event code on ${path}`, async ({ page }) => {
      await page.goto(path);
      await expect(page.locator("body")).toContainText(/Deluno|Overview|Activity|System/, { timeout: 15_000 });
      if (path === "/activity") {
        await page.getByRole("radio", { name: "Events" }).click();
      }
      // Let the feed and the live list settle before reading the page. Not
      // networkidle: the live connection and the pollers keep the network busy
      // by design (#1081).
      await expect(page.locator("#main-content [aria-busy='true']")).toHaveCount(0, { timeout: 15_000 });
      await page.waitForTimeout(1_500);
      const text = await page.locator("body").innerText();
      expect(text).not.toMatch(rawEventCode);
    });
  }

});
