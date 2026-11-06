import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1461: the sidebar's type matches Weir's, so switching apps does not shift it.
test("the sidebar's avatar letter, app name and link lines match Weir's at 2560 wide (#1461)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The sidebar is the desktop rail.");
  await page.setViewportSize({ width: 2560, height: 1249 });
  await authenticateAndNavigate(page, "/");
  const style = (locator: ReturnType<typeof page.locator>) =>
    locator.evaluate((el) => {
      const s = getComputedStyle(el);
      return { size: s.fontSize, weight: s.fontWeight, line: parseFloat(s.lineHeight) };
    });
  const avatar = await style(page.getByTestId("user-menu").locator("span").first());
  expect(avatar.size).toBe("12px");
  expect(avatar.weight).toBe("700");
  expect(avatar.line).toBeCloseTo(18.24, 1);
  const name = await style(page.getByRole("link", { name: "Deluno home" }).locator("span", { hasText: /^Deluno$/ }).first());
  expect(name.weight).toBe("700");
  expect(name.line).toBeCloseTo(24.32, 1);
  const link = await style(page.locator("aside [data-nav-label]").first());
  expect(link.line).toBeCloseTo(21.28, 1);
});
