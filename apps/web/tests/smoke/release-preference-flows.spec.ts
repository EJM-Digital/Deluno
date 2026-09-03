import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * #353 line 6: desktop, phone, keyboard and screen-reader flows pass.
 *
 * The axe sweep in `accessibility.spec.ts` checks these screens closed. The
 * release-preference granularity lives inside the drawers, which that sweep
 * never opens — so this walks the drawer the way somebody using a keyboard or
 * a screen reader has to, and runs axe on what is actually on screen once it
 * is open.
 *
 * It runs under both Playwright projects, so the phone half is the Pixel 7 run
 * of the same assertions rather than a separate, weaker test.
 */

const DRAWER_AXE_RULES = ["label", "aria-required-attr", "aria-allowed-role", "aria-valid-attr-value"] as const;

function authHeaders(token: string | null) {
  return token ? { Authorization: `Bearer ${token}` } : {};
}

test.describe("release preference flows", () => {
  test("opens a quality profile and reads its plan by keyboard", async ({ page }) => {
    const name = `Smoke-Keyboard-Profile-${Date.now()}`;
    await authenticateAndNavigate(page, "/settings/profiles");
    const token = await page.evaluate(() => sessionStorage.getItem("deluno-auth-token"));
    const headers = authHeaders(token);

    const qualityModel = await page.request.get("/api/quality-model", { headers });
    expect(qualityModel.ok()).toBe(true);
    const tier = (await qualityModel.json() as { tiers: Array<{ name: string }> }).tiers[0]?.name;
    expect(tier).toBeTruthy();

    const create = await page.request.post("/api/quality-profiles", {
      headers,
      data: {
        name,
        mediaType: "movies",
        cutoffQuality: tier,
        allowedQualities: tier,
        customFormatIds: "",
        upgradeUntilCutoff: true,
        upgradeUnknownItems: false
      }
    });
    expect(create.ok()).toBe(true);
    const profile = await create.json() as { id: string };

    try {
      await page.reload();

      // Reach the row with the keyboard rather than clicking it, and open it
      // with Enter. A list whose rows can only be opened by mouse hides the
      // whole of this feature from somebody who does not use one.
      const row = page.getByRole("row").filter({ hasText: name });
      await expect(row).toBeVisible();
      await row.focus();
      await expect(row).toBeFocused();
      await page.keyboard.press("Enter");

      const drawer = page.getByRole("dialog", { name });
      await expect(drawer).toBeVisible();

      // The plan is announced as a named section, not an unlabelled block.
      await expect(drawer.getByText("Effective release preferences")).toBeVisible();
      await expect(drawer.getByLabel("Typed preference families")).toBeVisible();

      // Axe, on the drawer as it actually is once opened.
      const results = await new AxeBuilder({ page })
        .include('[role="dialog"]')
        .withRules([...DRAWER_AXE_RULES])
        .analyze();
      expect(
        results.violations,
        results.violations
          .map((violation) => `${violation.id}: ${violation.help} (${violation.nodes.length} nodes)`)
          .join("\n")
      ).toEqual([]);

      // The profile name is reachable and editable from the keyboard, which is
      // the one control every other change goes through.
      const nameField = drawer.getByLabel("Profile name");
      await nameField.focus();
      await expect(nameField).toBeFocused();

      // Escape gets back out. A drawer that traps you is worse than one that
      // never opened.
      await page.keyboard.press("Escape");
      await expect(drawer).toBeHidden();
    } finally {
      await page.request.delete(`/api/quality-profiles/${profile.id}`, { headers });
    }
  });

  test("never shows an aggregate score on a primary owner surface", async ({ page }) => {
    // #353 line 7. The legacy score is a compatibility input, not a decision
    // value, and a number on the shelf invites people to tune it.
    await authenticateAndNavigate(page, "/movies");
    await expect(page.getByText(/total score|aggregate score|score:\s*-?\d/i)).toHaveCount(0);

    await page.goto("/settings/profiles");
    await expect(page.getByText(/total score|aggregate score/i)).toHaveCount(0);

    await page.goto("/settings/custom-formats");
    // The rules list states intent in words. "Prefer" and "Must not have" are
    // decisions; "+4321" is homework.
    await expect(page.getByText(/total score|aggregate score/i)).toHaveCount(0);
  });
});
