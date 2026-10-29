import { expect, test, type Page } from "@playwright/test";
import { fallbackCredentials, givePassword } from "../helpers/auth-helper";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Recovery codes on the real host, nothing stubbed (measure 4, issue 1392): the Account screen that shows how many are
 * left and makes a new set, the sign-in page's way to the recovery page, and a real reset that signs every session out.
 * The shared test account is put back to its own password at the end, whichever way a test went.
 */

const RECOVERED = "recovered-password-456";
const REFUSED = "That did not work. Check it and try again.";

async function makeCodesThroughTheApi(page: Page): Promise<string[]> {
  const made = await api<{ codes: string[] }>(page, "POST", "/api/auth/recovery-codes");
  expect(made.status).toBe(200);
  return made.body.codes;
}

/** Signs in as the shared account with whichever of the two passwords it has now, and puts it back to its own. */
async function restoreTheTestAccount(page: Page) {
  const request = page.context().request;
  const login = await request.post("/api/auth/login", { data: { username: fallbackCredentials.username, password: RECOVERED } });
  if (!login.ok()) return;
  const { accessToken } = (await login.json()) as { accessToken: string };
  await request.put("/api/auth/password", {
    headers: { Authorization: `Bearer ${accessToken}` },
    data: { currentPassword: RECOVERED, newPassword: fallbackCredentials.password }
  });
}

test.describe("Settings, Account", () => {
  test("is a tab among the Preferences tabs, says how many recovery codes are left, and a new set shows ten codes once", async ({ page }) => {
    const requests = recordRequests(page);
    await openScreen(page, "/settings/migration");
    await untilQuiet(page);
    await page.getByRole("navigation", { name: "Sections" }).getByRole("button", { name: "More" }).click();
    await page.getByRole("menuitem", { name: "Account", exact: true }).click();
    await expect(page).toHaveURL(/\/settings\/account/);
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    // The account was made through the real first-run route, which made its first ten codes.
    await expect(page.getByText(/^\d+ of 10 recovery codes left\.$/)).toBeVisible();

    await page.getByRole("button", { name: "Make new codes" }).click();
    const question = page.getByRole("dialog", { name: "Make new recovery codes?" });
    await expect(question).toContainText("The codes you have now stop working.");
    await question.getByRole("button", { name: "Make new codes" }).click();
    await givePassword(page);

    const codes = page.getByRole("list", { name: "Recovery codes" }).getByRole("listitem");
    await expect(codes).toHaveCount(10);
    for (const text of await codes.allInnerTexts()) expect(text).toMatch(/^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$/);
    await expect(page.getByRole("button", { name: "Done" })).toBeDisabled();
    await page.getByLabel("I have saved these codes").check();
    await page.getByRole("button", { name: "Done" }).click();

    await expect(page.getByText("10 of 10 recovery codes left.")).toBeVisible();
    requests.stop();
    expect(requests.find("POST", /^\/api\/auth\/recovery-codes$/).length, "the new set came from the server").toBe(1);
  });

  test("a code used up on the recovery page leaves nine", async ({ page }) => {
    test.setTimeout(90_000);
    await openScreen(page, "/settings/account");
    const codes = await makeCodesThroughTheApi(page);
    try {
      await page.context().clearCookies();
      await page.evaluate(() => localStorage.clear());
      await page.goto("/recover");
      await page.getByLabel("Recovery code").fill(codes[0]);
      await page.getByRole("button", { name: "Check code" }).click();
      await expect(page.getByText(fallbackCredentials.username)).toBeVisible();
      await page.getByLabel("New password", { exact: true }).fill(RECOVERED);
      await page.getByLabel("Confirm new password").fill(RECOVERED);
      await page.getByRole("button", { name: "Set new password" }).click();
      await expect(page.getByText("Your password is changed.")).toBeVisible();

      const login = await page.request.post("/api/auth/login", { data: { username: fallbackCredentials.username, password: RECOVERED } });
      expect(login.ok()).toBe(true);
      const { accessToken } = (await login.json()) as { accessToken: string };
      const status = await page.request.get("/api/auth/recovery-codes", { headers: { Authorization: `Bearer ${accessToken}` } });
      expect(await status.json()).toEqual({ remaining: 9, made: true });
    } finally {
      await restoreTheTestAccount(page);
    }
  });
});

test.describe("getting back in", () => {
  test("the sign-in page leads to the recovery page, which refuses a wrong code in plain words and says a fresh install is the only other way", async ({ page }) => {
    await page.goto("/login");
    await page.getByRole("link", { name: "Forgot your username or password?" }).click();
    await expect(page).toHaveURL(/\/recover/);
    await expect(page.getByRole("heading", { name: "Get back into Deluno" })).toBeVisible();

    const requests = recordRequests(page);
    await page.getByLabel("Recovery code").fill("AAAA-BBBB-CCCC");
    await page.getByRole("button", { name: "Check code" }).click();
    await expect(page.getByRole("alert")).toHaveText(REFUSED);
    // Still on the first step: nothing about the username or the code's parts was given away.
    await expect(page.getByLabel("New password", { exact: true })).toHaveCount(0);
    requests.stop();
    expect(requests.find("POST", /^\/api\/auth\/recovery\/check$/).length).toBe(1);

    await expect(page.getByText("If you have lost both your password and your recovery codes, the only way forward is a fresh install.")).toBeVisible();

    await page.getByRole("link", { name: "Back to sign in" }).click();
    await expect(page).toHaveURL(/\/login/);
  });

  test("a reset signs the old session out, keeps the API key working, and Activity says what happened", async ({ page }) => {
    test.setTimeout(90_000);
    await openScreen(page, "/settings/account");
    const codes = await makeCodesThroughTheApi(page);
    const made = await api<{ apiKey: string }>(page, "POST", "/api/api-keys", { name: "Recovery spec", scopes: "read" });
    expect(made.status).toBe(200);
    const { apiKey } = made.body;
    const oldToken = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
    try {
      const reset = await page.request.post("/api/auth/recovery/reset", { data: { code: codes[1], newPassword: RECOVERED } });
      expect(reset.status()).toBe(204);

      const old = await page.request.get("/api/auth/recovery-codes", { headers: { Authorization: `Bearer ${oldToken}` } });
      expect(old.status(), "the session from before the reset no longer works").toBe(401);
      const keyed = await page.request.get("/api/api-keys/scope-templates", { headers: { "X-Api-Key": apiKey } });
      expect(keyed.status(), "an API key keeps working").toBe(200);

      // The same code cannot be used twice.
      const again = await page.request.post("/api/auth/recovery/reset", { data: { code: codes[1], newPassword: "another-password-789" } });
      expect(again.status()).toBe(400);
      expect((await again.json()).message).toBe(REFUSED);

      // The page is signed out, so sign in again as the person would, with the new password.
      await page.goto("/login");
      await page.getByLabel("Username").fill(fallbackCredentials.username);
      await page.getByLabel("Password", { exact: true }).fill(RECOVERED);
      await page.getByRole("button", { name: "Sign in" }).click();
      await expect(page).not.toHaveURL(/\/login/);
      await page.goto("/activity");
      await untilQuiet(page);
      await expect(page.getByText("Password reset with a recovery code. Every signed-in session was ended.").first()).toBeVisible();
    } finally {
      await restoreTheTestAccount(page);
    }
  });
});
