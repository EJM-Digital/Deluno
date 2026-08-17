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

  for (const path of [
    "/",
    "/movies",
    "/movies/upgrades",
    "/tv",
    "/tv/upgrades",
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
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      await expect(page.getByText("This area could not load")).toHaveCount(0);
      await expect(page.locator("body")).toContainText(/Deluno|Overview|Movies|Settings|System|Calendar|Activity|Indexers|API/);
    });
  }

  test("desktop Library setup navigation expands in place and keeps child navigation clear", async ({ page }) => {
    test.skip(test.info().project.name === "mobile", "The desktop sidebar is replaced by the mobile navigation drawer.");

    await page.goto("/settings/media-management");

    const sidebar = page.locator("aside");
    const setupToggle = page.getByRole("button", { name: "Collapse Library setup" });
    const librarySection = page.getByRole("link", { name: "Library setup", exact: true });
    const processingAndImport = page.getByRole("link", { name: "File handling & naming", exact: true });

    await expect(sidebar).toBeVisible();
    expect(await sidebar.evaluate((element) => getComputedStyle(element).overflowX)).toBe("visible");
    await expect(setupToggle).toBeVisible();
    await expect(librarySection).toBeVisible();
    await expect(processingAndImport).toBeVisible();

    const parentBox = await librarySection.boundingBox();
    const childBox = await processingAndImport.boundingBox();
    expect(parentBox).not.toBeNull();
    expect(childBox).not.toBeNull();
    expect(childBox!.x).toBeGreaterThan(parentBox!.x);

    await setupToggle.click();
    await expect(processingAndImport).toBeHidden();
    await expect(page).toHaveURL(/\/settings\/media-management$/);

    await page.getByRole("button", { name: "Expand Library setup" }).click();
    await expect(processingAndImport).toBeVisible();

    await page.getByRole("link", { name: "Connections", exact: true }).click();
    await expect(page).toHaveURL(/\/indexers$/);
    const downloadClients = sidebar.getByRole("link", { name: "Download clients", exact: true });
    await expect(downloadClients).toBeVisible();
    await downloadClients.click();
    await expect(page).toHaveURL(/\/indexers\/download-clients$/);
    await expect(page.getByRole("heading", { name: "Download clients", exact: true })).toBeVisible();
    await expect(page.getByText("Remove items from the client queue", { exact: true })).toBeVisible();
  });

});
