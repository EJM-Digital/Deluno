import { expect, test } from "@playwright/test";

test.describe("protected navigation", () => {
  const protectedRoutes = [
    "/",
    "/movies",
    "/tv",
    "/calendar",
    "/indexers",
    "/activity",
    "/queue",
    "/settings",
    "/settings/general",
    "/settings/media-management",
    "/settings/destination-rules",
    "/settings/metadata",
    "/settings/profiles",
    "/system",
    "/system/backups",
    "/system/updates",
    "/system/api",
    "/system/docs"
  ];

  test("system API route is canonical and protected", async ({ page }) => {
    await page.goto("/system/api");

    await expect(authOrSetupHeading(page)).toBeVisible();
  });

  for (const route of protectedRoutes) {
    test(`protected route does not crash before auth: ${route}`, async ({ page }) => {
      await page.goto(route);

      await expect(authOrSetupHeading(page)).toBeVisible();
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      await expect(page.getByText("This area could not load")).toHaveCount(0);
    });
  }

  test("removed settings API route does not expose a stale page", async ({ page }) => {
    await page.goto("/settings/api");

    await expect(page).not.toHaveURL(/\/settings\/api$/);
  });
});

function authOrSetupHeading(page: import("@playwright/test").Page) {
  return page
    .getByRole("heading", { name: "Sign in to Deluno" })
    .or(page.getByRole("heading", { name: "Set up Deluno" }));
}
