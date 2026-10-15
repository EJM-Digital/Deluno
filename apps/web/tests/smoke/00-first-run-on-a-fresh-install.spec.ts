import { expect, test } from "@playwright/test";
import { fallbackCredentials } from "../helpers/auth-helper";

// The first account of a brand-new install, through the real host with nothing stubbed. This file sorts first, so it
// meets the install before any other spec has made the test account; once the account exists there is no fresh install
// left to meet, and the rest skip (the mobile project, which runs after the desktop one, always does).
test.describe.configure({ mode: "serial" });

test.describe("first run on a fresh install", () => {
  test.beforeEach(async ({ request }) => {
    // The host answers 503 here while it is still finishing its migrations; wait for a real answer.
    let status = await request.get("/api/auth/bootstrap-status");
    for (let attempt = 0; attempt < 60 && !status.ok(); attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 500)); // reason: bounded wait (30 s) for the host to finish starting
      status = await request.get("/api/auth/bootstrap-status");
    }
    const requiresSetup = status.ok() && ((await status.json()) as { requiresSetup?: boolean }).requiresSetup === true;
    test.skip(!requiresSetup, "The shared disposable install already has its account.");
  });

  test("opening the sign-in page or any protected page on a fresh install ends at account setup, never at the sign-in page", async ({ page }) => {
    await page.goto("/login");
    await expect(page).toHaveURL(/\/setup/);
    await expect(page.getByRole("heading", { name: "Set up Deluno" })).toBeVisible();

    await page.goto("/movies?filter=missing");
    await expect(page).toHaveURL(/\/setup/);
    await expect(page.getByRole("heading", { name: "Set up Deluno" })).toBeVisible();
  });

  test("a protected page on a fresh install goes to /setup carrying where the person was going", async ({ page }) => {
    await page.goto("/movies?filter=missing");
    await expect(page).toHaveURL(/\/setup\?return=%2Fmovies%3Ffilter%3Dmissing$/);
  });

  test("a status read that fails is read again, so the return address is still kept and nothing is taken as set up (#1339)", async ({ page }) => {
    let refused = 0;
    await page.route("**/api/auth/bootstrap-status", async (route) => {
      if (refused < 2) {
        refused++;
        await route.abort("failed");
        return;
      }
      await route.continue();
    });
    await page.goto("/movies?filter=missing");
    await expect(page).toHaveURL(/\/setup\?return=%2Fmovies%3Ffilter%3Dmissing$/);
    expect(refused).toBe(2);
  });

  test("when the status read keeps failing the person is told, and is not shown a sign-in form", async ({ page }) => {
    await page.route("**/api/auth/bootstrap-status", (route) => route.abort("failed"));
    await page.goto("/movies?filter=missing");
    await expect(page.getByText(/could not check whether it has been set up/i)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole("heading", { name: "Sign in to Deluno" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Sign in" })).toHaveCount(0);
  });

  test("a return address naming another site is not carried to the guided setup", async ({ page }) => {
    await page.goto("/setup?return=%2F%2Fother-host");
    await expect(page.getByRole("heading", { name: "Set up Deluno" })).toBeVisible();
    await page.getByLabel("Display name").fill("Test User");
    await page.getByLabel("Username").fill("test-user");
    await page.getByLabel("Password", { exact: true }).fill("short");
    await page.getByLabel("Confirm password").fill("short");
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page.getByText("Use at least 8 characters for the password.")).toBeVisible();
    await expect(page).toHaveURL(/\/setup\?return=%2F%2Fother-host$/);
  });

  test("a fresh install shows Set up Deluno with its four fields and Create account waits until all four have text", async ({ page }) => {
    await page.goto("/setup");
    await expect(page.getByRole("heading", { name: "Set up Deluno" })).toBeVisible();
    const create = page.getByRole("button", { name: "Create account" });
    await expect(create).toBeDisabled();

    await page.getByLabel("Display name").fill("Test User");
    await page.getByLabel("Username").fill("test-user");
    await page.getByLabel("Password", { exact: true }).fill("password-123");
    await expect(create).toBeDisabled();
    await page.getByLabel("Confirm password").fill("different");
    await expect(create).toBeEnabled();
  });

  test("a short password and a mismatched confirmation are refused in plain words and nothing is sent", async ({ page }) => {
    const posted: string[] = [];
    page.on("request", (request) => {
      if (request.method() === "POST" && request.url().includes("/api/auth/bootstrap")) posted.push(request.url());
    });
    await page.goto("/setup");
    await page.getByLabel("Display name").fill("Test User");
    await page.getByLabel("Username").fill("test-user");
    await page.getByLabel("Password", { exact: true }).fill("short");
    await page.getByLabel("Confirm password").fill("short");
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page.getByText("Use at least 8 characters for the password.")).toBeVisible();

    await page.getByLabel("Password", { exact: true }).fill("password-123");
    await page.getByLabel("Confirm password").fill("password-124");
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page.getByText("Passwords do not match.")).toBeVisible();

    expect(posted).toEqual([]);
    await expect(page).toHaveURL(/\/setup/);
  });

  test("creating the account opens the guided setup carrying the return address, signs the person in, stores the session, and a reload keeps them signed in", async ({ page }) => {
    await page.goto("/setup?return=%2Fmovies");
    await page.getByLabel("Display name").fill(fallbackCredentials.displayName);
    await page.getByLabel("Username").fill(fallbackCredentials.username);
    await page.getByLabel("Password", { exact: true }).fill(fallbackCredentials.password);
    await page.getByLabel("Confirm password").fill(fallbackCredentials.password);
    await page.getByRole("button", { name: "Create account" }).click();

    await expect(page).toHaveURL(/\/setup-guide\?return=%2Fmovies$/);
    await expect.poll(() => page.evaluate(() => window.localStorage.getItem("deluno-auth-token"))).toBeTruthy();
    await page.goto("/");
    await expect(page).not.toHaveURL(/\/(setup|login)/);
    await expect(page.getByText(fallbackCredentials.displayName).first()).toBeVisible();
  });
});
