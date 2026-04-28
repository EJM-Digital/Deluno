import { expect, test } from "@playwright/test";

const fallbackCredentials = {
  displayName: "Deluno Smoke User",
  username: "deluno-smoke",
  password: "deluno-smoke-password"
};

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

  for (const path of [
    "/",
    "/movies",
    "/tv",
    "/calendar",
    "/indexers",
    "/activity",
    "/queue",
    "/settings",
    "/settings/media-management",
    "/settings/destination-rules",
    "/settings/profiles",
    "/settings/quality",
    "/settings/custom-formats",
    "/settings/lists",
    "/settings/metadata",
    "/settings/general",
    "/settings/ui",
    "/system",
    "/system/api",
    "/system/docs"
  ]) {
    test(`loads ${path} with a signed-in user`, async ({ page }) => {
      await page.goto(path);
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      await expect(page.getByText("This area could not load")).toHaveCount(0);
      await expect(page.locator("body")).toContainText(/Deluno|Overview|Movies|Settings|System|Calendar|Activity|Indexers|API/);
    });
  }

  test("opens full movie and TV workspaces from library pages", async ({ page }) => {
    await page.goto("/movies");
    const movieLink = page.locator('a[href^="/movies/"]').first();
    if ((await movieLink.count()) > 0) {
      await movieLink.click();
      await expect(page).toHaveURL(/\/movies\/[^/]+$/);
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      await expect(page.getByText("This area could not load")).toHaveCount(0);
      await expect(page.locator("body")).toContainText(/Movie workspace|Search and dispatch|Metadata/);
    }

    await page.goto("/tv");
    const tvLink = page.locator('a[href^="/tv/"]').first();
    if ((await tvLink.count()) > 0) {
      await tvLink.click();
      await expect(page).toHaveURL(/\/tv\/[^/]+$/);
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      await expect(page.getByText("This area could not load")).toHaveCount(0);
      await expect(page.locator("body")).toContainText(/TV workspace|Search and dispatch|Metadata|Episodes/);
    }
  });
});
