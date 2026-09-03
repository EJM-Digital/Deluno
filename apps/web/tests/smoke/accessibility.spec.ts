import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

const ruleIds = ["label", "aria-required-attr", "aria-allowed-role", "th-has-data-cells"] as const;
const screens = [
  "/movies",
  "/setup-guide",
  "/system",
  "/system/audit",
  "/system/backups",
  "/system/updates",
  "/settings/general",
  "/settings/metadata",
  // #353 line 6: the release-preference surfaces are where the granularity
  // lives, so they are exactly the screens where a label or a required-state
  // that only exists visually stops somebody using the product.
  "/settings/profiles",
  "/settings/custom-formats",
  "/settings/policy-sets",
  "/settings/release-rules"
] as const;

test.describe("accessibility semantics", () => {
  for (const path of screens) {
    test(`has no targeted axe violations on ${path}`, async ({ page }) => {
      await authenticateAndNavigate(page, path);

      const results = await new AxeBuilder({ page })
        .withRules([...ruleIds])
        .analyze();

      expect(
        results.violations,
        results.violations
          .map((violation) => `${violation.id}: ${violation.help} (${violation.nodes.length} nodes)`)
          .join("\n")
      ).toEqual([]);
    });
  }

  test("keeps setup presets keyboard-navigable as radio groups", async ({ page }) => {
    await authenticateAndNavigate(page, "/setup-guide");
    await page.getByRole("button", { name: /Find & Download/ }).click();

    const searchPresets = page.getByRole("radiogroup", { name: "Search source presets" });
    const searchOptions = searchPresets.getByRole("radio");
    await expect(searchOptions).toHaveCount(3);
    await searchOptions.first().click();
    await expect(searchOptions.first()).toHaveAttribute("aria-checked", "true");
    await searchOptions.first().press("ArrowRight");
    await expect(searchOptions.nth(1)).toBeFocused();

    const clientPresets = page.getByRole("radiogroup", { name: "Download client presets" });
    const clientOptions = clientPresets.getByRole("radio");
    await expect(clientOptions).toHaveCount(6);
    await clientOptions.first().click();
    await clientOptions.first().press("ArrowDown");
    await expect(clientOptions.nth(1)).toBeFocused();
  });
});
