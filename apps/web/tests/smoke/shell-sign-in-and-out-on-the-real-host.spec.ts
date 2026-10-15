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
});
