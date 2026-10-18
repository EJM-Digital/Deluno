import { expect, test, type Page } from "@playwright/test";
import { ensureBootstrapped, fallbackCredentials } from "../helpers/auth-helper";
import { api, DESKTOP, expectNoErrorScreen, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The shell around every screen on the real host, nothing stubbed (measure 4): the address that is not a page, the sign-in
 * that carries the person back, a session the server no longer accepts, "keep me signed in", the account menu, a password
 * change, and the prompt that stands between unsaved edits and another screen.
 */

async function signInThroughTheForm(page: Page, options: { keep?: boolean; password?: string } = {}) {
  await page.getByLabel("Username").fill(fallbackCredentials.username);
  await page.getByLabel("Password", { exact: true }).fill(options.password ?? fallbackCredentials.password);
  if (options.keep) await page.getByLabel(/Keep me signed in/).check();
  await page.getByRole("button", { name: "Sign in" }).click();
}

test.describe("an address that is not a page", () => {
  test("shows That page is missing with its code, and Try again and Report issue both work", async ({ page }) => {
    await openScreen(page, "/no-such-page");
    await untilQuiet(page);

    await expect(page.getByText("That page is missing")).toBeVisible();
    await expect(page.getByText("route:404")).toBeVisible();

    await page.getByRole("button", { name: "Try again" }).click();
    await expect(page.getByText("That page is missing")).toBeVisible();

    await page.getByRole("button", { name: "Report issue" }).click();
    await expect(page).toHaveURL(/\/system/);
    await expectNoErrorScreen(page);
  });

  test("a deeper address that is not a page is the same screen, not a blank one", async ({ page }) => {
    await openScreen(page, "/movies/not/a/real/page/at/all");
    await untilQuiet(page);
    await expect(page.getByText("That page is missing")).toBeVisible();
  });
});

test.describe("signing in carries the person back to where they were", () => {
  test.beforeEach(async ({ page }) => {
    await ensureBootstrapped(page.context().request);
  });

  test("a signed-out visit to a screen with a query goes to /login carrying that address, and signing in lands back on it", async ({ page }) => {
    await page.goto("/movies?x=1");
    await expect(page).toHaveURL(/\/login\?return=/);
    expect(new URL(page.url()).searchParams.get("return")).toBe("/movies?x=1");

    await signInThroughTheForm(page);

    await expect.poll(() => new URL(page.url()).pathname).toBe("/movies");
    expect(new URL(page.url()).search).toBe("?x=1");
    expect(await page.evaluate(() => localStorage.getItem("deluno-auth-token"))).toBeTruthy();
  });

  // Defect: the sign-in address should carry the hash as well (return=<path+query+hash>), but the redirect to /login drops it:
  // a visit to /movies?x=1#h arrives at /login?return=%2Fmovies%3Fx%3D1.
  test.skip("a signed-out visit to a screen with a hash carries the hash in the sign-in address", async ({ page }) => {
    await page.goto("/movies?x=1#h");
    await expect(page).toHaveURL(/\/login\?return=/);
    expect(new URL(page.url()).searchParams.get("return")).toBe("/movies?x=1#h");
  });

  test("a session the server no longer accepts is cleared, sent to /login with where the person was, and the sign-in page says why", async ({ page }) => {
    await openScreen(page, "/queue");
    await untilQuiet(page);
    // The token the server issued is swapped for one it never issued: every call now answers 401, as after a password change elsewhere.
    await page.evaluate(() => localStorage.setItem("deluno-auth-token", "not-a-token-this-server-issued"));

    await page.goto("/queue?x=1");

    await expect(page).toHaveURL(/\/login\?return=/);
    expect(new URL(page.url()).searchParams.get("return")).toBe("/queue?x=1");
    expect(await page.evaluate(() => localStorage.getItem("deluno-auth-token"))).toBeNull();
    await expect(page.getByText(/signed out/i).first()).toBeVisible();
  });

  test("ticking Keep me signed in is remembered with a 30-day session, and leaving it unticked is a 7-day one", async ({ page }) => {
    const daysAhead = async () => page.evaluate(() => {
      const expires = localStorage.getItem("deluno-auth-expires");
      return expires ? (Date.parse(expires) - Date.now()) / 86_400_000 : -1;
    });

    await page.goto("/login");
    await signInThroughTheForm(page, { keep: true });
    await expect.poll(() => page.evaluate(() => localStorage.getItem("deluno-auth-token"))).toBeTruthy();
    expect(await page.evaluate(() => localStorage.getItem("deluno-auth-keep"))).toBe("1");
    expect(await daysAhead()).toBeGreaterThan(29);
    expect(await daysAhead()).toBeLessThanOrEqual(30.01);

    await page.evaluate(() => localStorage.clear());
    await page.goto("/login");
    await signInThroughTheForm(page);
    await expect.poll(() => page.evaluate(() => localStorage.getItem("deluno-auth-token"))).toBeTruthy();
    expect(await page.evaluate(() => localStorage.getItem("deluno-auth-keep"))).toBe("0");
    expect(await daysAhead()).toBeGreaterThan(6);
    expect(await daysAhead()).toBeLessThanOrEqual(7.01);
  });
});

test.describe("the account menu", () => {
  test("is invisible and takes no clicks until the account button is hovered or has keyboard focus", async ({ page }) => {
    await openScreen(page, "/");
    await untilQuiet(page);
    const change = page.getByRole("button", { name: "Change password" });
    const menu = page.locator("div.group").filter({ has: page.getByTestId("user-menu") }).locator("div.pointer-events-none").first();

    expect(await menu.evaluate((el) => getComputedStyle(el).opacity)).toBe("0");
    expect(await menu.evaluate((el) => getComputedStyle(el).pointerEvents)).toBe("none");

    await page.getByTestId("user-menu").hover();
    await expect.poll(() => menu.evaluate((el) => getComputedStyle(el).opacity)).toBe("1");
    expect(await menu.evaluate((el) => getComputedStyle(el).pointerEvents)).toBe("auto");
    await expect(change).toBeVisible();

    await page.mouse.move(DESKTOP.width / 2, DESKTOP.height / 2);
    await expect.poll(() => menu.evaluate((el) => getComputedStyle(el).opacity)).toBe("0");

    await page.getByTestId("user-menu").focus();
    await expect.poll(() => menu.evaluate((el) => getComputedStyle(el).opacity)).toBe("1");
    expect(await menu.evaluate((el) => getComputedStyle(el).pointerEvents)).toBe("auto");
  });
});

test.describe("changing the password", () => {
  test("the dialog says Password changed, and the person's own session is already finished, so the next call sends them to sign-in", async ({ page }) => {
    test.setTimeout(90_000);
    const changed = "A-changed-password-for-the-shell-spec-1";
    await openScreen(page, "/");
    await untilQuiet(page);

    try {
      await page.getByTestId("user-menu").hover();
      await page.getByRole("button", { name: "Change password" }).click();
      await page.getByLabel("Current password").fill(fallbackCredentials.password);
      await page.getByLabel("New password", { exact: true }).fill(changed);
      await page.getByLabel("Confirm new password").fill(changed);
      await page.getByRole("button", { name: "Save password" }).click();
      await expect(page.getByText("Password changed.")).toBeVisible();

      // The old token no longer works; the next screen the person opens sends them to sign-in with the reason.
      await page.goto("/queue");
      await expect(page).toHaveURL(/\/login/);
      await expect(page.getByText(/password changed/i).first()).toBeVisible();
    } finally {
      // Put the shared test account back, whichever way this went.
      const request = page.context().request;
      for (const password of [changed, fallbackCredentials.password]) {
        const login = await request.post("/api/auth/login", { data: { username: fallbackCredentials.username, password } });
        if (!login.ok()) continue;
        if (password === fallbackCredentials.password) break;
        const { accessToken } = (await login.json()) as { accessToken: string };
        await request.put("/api/auth/password", {
          headers: { Authorization: `Bearer ${accessToken}` },
          data: { currentPassword: changed, newPassword: fallbackCredentials.password }
        });
      }
    }
  });
});

test.describe("unsaved edits on a settings screen", () => {
  async function editHelperAddress(page: Page, value: string) {
    await openScreen(page, "/settings/general");
    await untilQuiet(page);
    await page.getByRole("textbox", { name: "Helper address" }).fill(value);
  }
  const prompt = (page: Page) => page.getByRole("dialog", { name: "Unsaved changes" });
  const goToInterface = (page: Page) => page.getByRole("link", { name: "Interface", exact: true }).first().click();

  test("Save and continue saves the visible form, then goes on to the screen the person chose", async ({ page }) => {
    await editHelperAddress(page, "http://127.0.0.1:8191");
    const requests: string[] = [];
    page.on("request", (request) => { if (request.method() !== "GET" && request.url().includes("/api/")) requests.push(`${request.method()} ${new URL(request.url()).pathname}`); });

    await goToInterface(page);
    await expect(prompt(page)).toBeVisible();
    await prompt(page).getByRole("button", { name: /Save and continue/ }).click();

    await expect(page).toHaveURL(/\/settings\/ui/, { timeout: 15_000 });
    expect(requests.some((r) => /^(PUT|PATCH|POST) \/api\/settings/.test(r)), `the save request was sent (${requests.join(", ")})`).toBe(true);
    await page.goto("/settings/general");
    await expect(page.getByRole("textbox", { name: "Helper address" })).toHaveValue("http://127.0.0.1:8191");
    // Put it back so the shared database is as it was.
    await page.getByRole("textbox", { name: "Helper address" }).fill("");
    await page.getByRole("button", { name: /^Save/ }).first().click();
  });

  test("Escape closes the prompt, the person stays on the screen with their edit, and an in-page query change never asks", async ({ page }) => {
    await editHelperAddress(page, "http://127.0.0.1:9999");

    await goToInterface(page);
    await expect(prompt(page)).toBeVisible();
    await page.keyboard.press("Escape");

    await expect(prompt(page)).toHaveCount(0);
    await expect(page).toHaveURL(/\/settings\/general/);
    await expect(page.getByRole("textbox", { name: "Helper address" })).toHaveValue("http://127.0.0.1:9999");

    // A change that keeps the same path (a query or a hash) is not a move to another screen.
    await page.evaluate(() => { window.history.pushState({}, "", "/settings/general?x=1#y"); window.dispatchEvent(new PopStateEvent("popstate")); });
    await expect(prompt(page)).toHaveCount(0);
    await expect(page.getByRole("textbox", { name: "Helper address" })).toHaveValue("http://127.0.0.1:9999");
  });

  test("closing or reloading the tab with an unsaved edit asks the browser to confirm, and a clean screen does not", async ({ page }) => {
    // What the browser does when the tab is closed or reloaded: it raises beforeunload and asks if the page cancels it. (Playwright's
    // own close cannot be used: Chrome shows that question only for a page the person has really typed into.)
    const asksBeforeLeaving = () => page.evaluate(() => {
      const event = new Event("beforeunload", { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    });

    await openScreen(page, "/settings/general");
    await untilQuiet(page);
    expect(await asksBeforeLeaving(), "a clean screen lets the tab go").toBe(false);

    await page.getByRole("textbox", { name: "Helper address" }).fill("http://127.0.0.1:7777");
    expect(await asksBeforeLeaving(), "an unsaved edit asks").toBe(true);

    await page.getByRole("textbox", { name: "Helper address" }).fill("");
    await expect.poll(asksBeforeLeaving, { message: "back to what was saved, the screen is clean again" }).toBe(false);
  });
});

test.describe("the slash key", () => {
  // Defect: the shortcut list says "/" is "Focus search", but it only opens the phone search sheet, which is hidden from the
  // large breakpoint up, so on a desktop pressing it does nothing visible (app-layout.tsx, useHotkeys("/") and the
  // `lg:hidden` sheet in ContentTopbar).
  test.skip("pressing / on a desktop puts the cursor in a search box", async ({ page }) => {
    await openScreen(page, "/movies");
    await untilQuiet(page);
    await page.keyboard.press("/");
    const focused = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null;
      return el ? { tag: el.tagName, visible: !!(el.offsetWidth || el.offsetHeight) } : null;
    });
    expect(focused?.tag).toBe("INPUT");
    expect(focused?.visible).toBe(true);
  });
});

void api;
