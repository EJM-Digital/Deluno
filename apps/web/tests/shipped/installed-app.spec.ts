import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * #291 — the journeys that must run against the thing that actually ships.
 *
 * Every other test in this suite loads the UI from `serve-smoke-preview.mjs`,
 * which does not exist in a real install. There, `Deluno.Host` serves its own
 * static files and its own SPA fallback. That gap let an app that rendered as
 * raw source in every browser pass 260 tests.
 *
 * These run under the `shipped` project, whose baseURL is the host itself.
 * They are deliberately few: this is not a second UI suite, it is the answer to
 * "does the installed app work at all". Anything about *how the binary serves
 * its front end* — content types, the fallback, asset paths — belongs here.
 */
test.describe("the installed app", () => {
  test("renders as an application, not as its own source", async ({ page }) => {
    await page.goto("/");

    // The failure this exists for: nosniff plus a missing Content-Type made the
    // browser print the markup. A page showing its own source has the document
    // sitting in one text node and no element tree at all, so the test is
    // whether anything Deluno rendered is on screen. Retrying assertions only —
    // `load` fires before React has mounted, and a one-shot count would race it.
    await expect(page.locator("#root")).toBeAttached();
    await expect(page.locator("#root *").first()).toBeAttached();
    await expect(page.locator("body")).not.toContainText("<!doctype html>", { ignoreCase: true });
  });

  test("serves its script bundle as script", async ({ page, request }) => {
    // nosniff means a bundle sent as text/plain is refused and the app never
    // boots — the same class of failure as the HTML one, one layer down.
    //
    // The bundle is content-hashed, so its name is read off the shell rather
    // than assumed. That hashing is the point: a fixed name let a deployed fix
    // keep serving out of a browser cache and look like a fix that had failed.
    await page.goto("/");
    const bundle = await page.locator('script[src*="/assets/deluno."]').first().getAttribute("src");

    expect(bundle, "the app shell should name a hashed entry bundle").toMatch(/\/assets\/deluno\.[^.]+\.js$/);

    const response = await request.get(bundle!);

    expect(response.status()).toBe(200);
    expect(response.headers()["content-type"] ?? "").toContain("javascript");
  });

  test("signs a user in", async ({ page }) => {
    await authenticateAndNavigate(page, "/");

    await expect(page.locator("nav").first()).toBeVisible();
    await expect(page).not.toHaveURL(/\/login/);
  });

  test("serves a deep link cold, without a client-side navigation first", async ({ page }) => {
    await authenticateAndNavigate(page, "/");

    // A bookmark, a refresh, or a shared link all land on the SPA fallback with
    // no JavaScript already running. This is the case a preview server that
    // rewrites everything to index.html can never distinguish from a broken one.
    const response = await page.goto("/settings/libraries");
    expect(response?.status()).toBe(200);
    expect(response?.headers()["content-type"] ?? "").toContain("text/html");
    await expect(page.getByRole("heading", { name: /Libraries/i }).first()).toBeVisible();
  });

  test("survives a reload after navigating within the app", async ({ page }) => {
    await authenticateAndNavigate(page, "/");
    await page.goto("/queue");
    await page.reload();

    await expect(page.locator("nav").first()).toBeVisible();
    await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
  });

  test("does not hand the app shell back for an unknown API path", async ({ request }) => {
    // If the fallback swallows API 404s, a broken client call looks like a
    // successful page load and the real fault is invisible.
    const response = await request.get("/api/definitely-not-a-route");

    expect(response.status()).toBe(404);
    expect(await response.text()).not.toContain('<div id="root">');
  });
});
