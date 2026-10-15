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

  test("creating the account signs the person in, stores the session, and a reload keeps them signed in", async ({ page }) => {
    await page.goto("/setup");
    await page.getByLabel("Display name").fill(fallbackCredentials.displayName);
    await page.getByLabel("Username").fill(fallbackCredentials.username);
    await page.getByLabel("Password", { exact: true }).fill(fallbackCredentials.password);
    await page.getByLabel("Confirm password").fill(fallbackCredentials.password);
    await page.getByRole("button", { name: "Create account" }).click();

    await expect(page).not.toHaveURL(/\/setup$/);
    await expect.poll(() => page.evaluate(() => window.localStorage.getItem("deluno-auth-token"))).toBeTruthy();
    await page.reload();
    await expect(page).not.toHaveURL(/\/(setup|login)/);
    await expect(page.getByText(fallbackCredentials.displayName).first()).toBeVisible();
  });
});

// DEFECT (found 15 Oct 2026): after Create account the person lands on "/" and not on the guided setup. SetupPage navigates
// to /setup-guide?return=... once bootstrap() resolves, but bootstrap() has already signed them in, and the page's own
// "already signed in, go to return" effect runs first and sends them to "/" (the `return` address). The test below is the
// behaviour the register row asks for; it stays fixme until the page goes to the guide.
test.describe("first run on a fresh install: the guided setup opens", () => {
  test.fixme(true, "DEFECT: creating the account lands on / instead of /setup-guide (the signed-in redirect effect beats the explicit navigate)");

  test("creating the account opens the guided setup carrying the return address", async ({ page }) => {
    await page.goto("/setup?return=%2Fmovies");
    await page.getByLabel("Display name").fill(fallbackCredentials.displayName);
    await page.getByLabel("Username").fill(fallbackCredentials.username);
    await page.getByLabel("Password", { exact: true }).fill(fallbackCredentials.password);
    await page.getByLabel("Confirm password").fill(fallbackCredentials.password);
    await page.getByRole("button", { name: "Create account" }).click();

    await expect(page).toHaveURL(/\/setup-guide\?return=%2Fmovies$/);
  });
});

// DEFECT (found 15 Oct 2026): a protected page opened on a fresh install does not always carry where the person was going.
// router.tsx's requiresSetup() answers false when the status read fails, and the read is sometimes aborted by the router's own
// second navigation (net::ERR_ABORTED on /api/auth/bootstrap-status in a recorded run). The page then goes to
// /login?return=..., whose loader finds the install needs setup and goes to /setup, with the return address lost. About half
// the runs end at "/setup" and not "/setup?return=%2Fmovies%3Ffilter%3Dmissing".
test.describe("first run on a fresh install: the return address is kept", () => {
  test.fixme(true, "DEFECT: requiresSetup() treats an aborted bootstrap-status read as already set up, so the return address is sometimes lost");

  test("a protected page on a fresh install goes to /setup carrying where the person was going", async ({ page }) => {
    await page.goto("/movies?filter=missing");
    await expect(page).toHaveURL(/\/setup\?return=%2Fmovies%3Ffilter%3Dmissing$/);
  });
});
