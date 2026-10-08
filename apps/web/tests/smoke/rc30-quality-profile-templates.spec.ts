import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30 live test, #1139. A person makes quality profiles for a test matrix on
// Quality & Release > Quality Profiles: a custom TV profile used to get "No formats",
// the TV template's name repeated "TV", and there was no TV 4K, 720p or SD template.

test("the templates cover TV in 4K, 720p and SD, and a custom TV profile starts with formats (#1139)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The drawer is the same on a phone; the desktop run proves the behaviour.");
  test.setTimeout(60_000);
  await authenticateAndNavigate(page, "/settings/profiles");
  const name = `Custom TV Matrix ${Date.now()}`;
  let profileId: string | null = null;

  try {
    await page.getByRole("button", { name: "New quality profile" }).first().click();
    const drawer = page.getByRole("dialog", { name: "New quality profile" });
    const template = drawer.getByLabel("Choose a template");

    const labels = (await template.locator("option").allInnerTexts()).map((label) => label.trim());
    const tv = labels.filter((label) => label.endsWith("· TV"));
    expect(tv.some((label) => /4K/i.test(label)), `TV templates: ${tv.join(" | ")}`).toBe(true);
    expect(tv.some((label) => /720p/i.test(label)), `TV templates: ${tv.join(" | ")}`).toBe(true);
    expect(tv.some((label) => /\bSD\b|DVD/i.test(label)), `TV templates: ${tv.join(" | ")}`).toBe(true);
    // The TV template's name does not say TV twice ("TV Shows / TV ...").
    for (const label of tv) expect(label, `"${label}" repeats TV`).not.toMatch(/\bTV\b.*\bTV\b.*\bTV\b|TV [—-] /);

    // A custom TV profile: no template, TV shows, a name, Create.
    await drawer.getByRole("radio", { name: "TV shows" }).click();
    await drawer.getByLabel("Profile name").fill(name);
    const created = page.waitForResponse((response) => response.request().method() === "POST" && new URL(response.url()).pathname.startsWith("/api/quality-profiles"));
    await drawer.getByRole("button", { name: "Create quality profile" }).click();
    const response = await created;
    expect(response.ok(), `POST /api/quality-profiles failed: ${response.status()}`).toBe(true);
    profileId = (await response.json() as { id: string }).id;

    // The saved profile opens, then the list shows its row with formats, not "No formats".
    await expect(page.getByRole("dialog", { name })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog", { name })).toHaveCount(0);
    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row).toBeVisible();
    await expect(row.getByText(/^\d+ formats?$/)).toBeVisible();
    await expect(row.getByText("No formats", { exact: true })).toHaveCount(0);
  } finally {
    if (profileId) {
      const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
      await page.request.delete(`/api/quality-profiles/${profileId}`, { headers: { Authorization: `Bearer ${token}` }, timeout: 10_000 }).catch(() => undefined);
    }
  }
});
