import { expect, test } from "@playwright/test";
import { ensureBootstrapped, fallbackCredentials } from "../helpers/auth-helper";

// Nothing is stubbed: the form talks to the real host's real sign-in, sign-out and session routes.
test.describe("signing in and out through the real host", () => {
  test.beforeEach(async ({ page }) => {
    await ensureBootstrapped(page.context().request);
  });

  test("wrong credentials show the refusal in the card and keep the person on the sign-in page with no session", async ({ page }) => {
    await page.goto("/login");
    await page.getByLabel("Username").fill(fallbackCredentials.username);
    await page.getByLabel("Password", { exact: true }).fill("not-the-password-at-all");
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page.locator("form").getByText(/\S/).first()).toBeVisible();
    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole("heading", { name: "Sign in to Deluno" })).toBeVisible();
    expect(await page.evaluate(() => window.localStorage.getItem("deluno-auth-token"))).toBeNull();
  });

  test("correct credentials sign the person in and move them to where they were going, and Sign out ends the session on the server", async ({ page }) => {
    await page.goto("/login?return=%2Fsettings");
    await page.getByLabel("Username").fill(fallbackCredentials.username);
    await page.getByLabel("Password", { exact: true }).fill(fallbackCredentials.password);
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page).toHaveURL(/\/settings/);
    const token = await page.evaluate(() => window.localStorage.getItem("deluno-auth-token"));
    expect(token).toBeTruthy();
    const open = await page.request.get("/api/tags", { headers: { Authorization: `Bearer ${token}` } });
    expect(open.status()).toBe(200);

    await page.getByRole("button", { name: "Sign out" }).first().dispatchEvent("click");

    await expect(page).toHaveURL(/\/login/);
    expect(await page.evaluate(() => window.localStorage.getItem("deluno-auth-token"))).toBeNull();
    const after = await page.request.get("/api/tags", { headers: { Authorization: `Bearer ${token}` } });
    expect(after.status()).toBe(401);
  });

  for (const [label, address] of [["//other-host", "%2F%2Fother-host"], ["a backslash", "%2F%5Cother-host"], ["a full address", "https%3A%2F%2Fother-host%2F"], ["javascript:", "javascript%3Aalert(1)"]]) {
    test(`signing in with a return address of ${label} stays on this app and lands on the home page (#1339)`, async ({ page }) => {
      await page.goto(`/login?return=${address}`);
      await page.getByLabel("Username").fill(fallbackCredentials.username);
      await page.getByLabel("Password", { exact: true }).fill(fallbackCredentials.password);
      await page.getByRole("button", { name: "Sign in" }).click();

      await expect.poll(() => page.evaluate(() => window.localStorage.getItem("deluno-auth-token"))).toBeTruthy();
      await expect.poll(() => new URL(page.url()).pathname).toBe("/");
      expect(new URL(page.url()).origin).toBe(new URL(test.info().project.use.baseURL ?? page.url()).origin);
    });
  }

  test("on an install that is set up, /setup with a return address naming another site goes to the sign-in page, not that site (#1339)", async ({ page }) => {
    await page.goto("/setup?return=%2F%2Fother-host");
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole("heading", { name: "Sign in to Deluno" })).toBeVisible();
  });
});
