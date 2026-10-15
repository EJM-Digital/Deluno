import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// Nothing is on by default (#936): on the real host, with nothing stubbed, a new install's Dashboard says automation is
// paused until the person resumes it, and the strip goes away on Resume and comes back when it is paused again.
test("a new install shows the paused strip until automation is resumed, then not", async ({ page }) => {
  await authenticateAndNavigate(page, "/");
  const token = await page.evaluate(() => window.localStorage.getItem("deluno-auth-token"));
  const settings = async () => (await (await page.request.get("/api/settings", { headers: { Authorization: `Bearer ${token}` } })).json()) as { autoStartJobs: boolean };
  test.skip((await settings()).autoStartJobs, "Automation was already resumed on this shared disposable install.");

  const strip = page.locator('[data-automation-paused="dashboard"]');
  await expect(strip).toBeVisible();
  await expect(strip).toContainText("Automation is paused");

  try {
    await strip.getByRole("button", { name: "Resume automation" }).click();
    await expect(page.getByText("Automation resumed")).toBeVisible();
    await expect(strip).toHaveCount(0);
    expect((await settings()).autoStartJobs).toBe(true);
  } finally {
    // The rest of the suite shares this install and expects nothing to run by itself.
    await page.request.put("/api/settings/automation", { headers: { Authorization: `Bearer ${token}` }, data: { isEnabled: false } });
  }
  await page.reload();
  await expect(page.locator('[data-automation-paused="dashboard"]')).toBeVisible();
});
