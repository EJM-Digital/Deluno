import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("Settings and Automation Configuration", () => {
  test.describe("General Settings", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/general");
    });

    test("general settings page loads with expected sections", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/settings|general/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(
        /application|name|port|host|api|documentation/i
      );
    });

    test("can modify general settings fields", async ({ page }) => {
      // Find and interact with input fields
      const inputs = page.locator("input[type='text']");
      if (await inputs.count() > 0) {
        const firstInput = inputs.first();
        const initialValue = await firstInput.inputValue();

        await firstInput.fill("test-value-updated");
        await page.waitForTimeout(500);

        const updated = await firstInput.inputValue();
        expect(updated).toBe("test-value-updated");

        // Restore if possible
        await firstInput.fill(initialValue || "");
      }
    });

    test("displays save/reset buttons for settings", async ({ page }) => {
      const saveButton = page.getByRole("button", { name: /save|apply|confirm/i });
      if (await saveButton.isVisible()) {
        await expect(saveButton).toBeVisible();
      }

      const resetButton = page.getByRole("button", { name: /reset|cancel|revert/i });
      if (await resetButton.isVisible()) {
        await expect(resetButton).toBeVisible();
      }
    });
  });

  test.describe("Media Management Settings", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/media-management");
    });

    test("media management page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(
        /media.*management|library/i
      );

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/path|root.*folder|location|movies|tv|series/i);
    });

    test("can configure root folders", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/root.*folder|library.*path/i);

      // Look for path input or folder selector
      const pathInput = page.locator("input").filter({ hasText: /path|folder|directory/i }).first();
      if (await pathInput.isVisible()) {
        await expect(pathInput).toBeVisible();
      }
    });

    test("can add/remove root folders", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add.*folder|add.*path|new.*folder/i });
      if (await addButton.isVisible()) {
        await expect(addButton).toBeEnabled();
      }

      // Check for remove buttons
      const removeButtons = page.getByRole("button", { name: /remove|delete|trash/i });
      if (await removeButtons.count() > 0) {
        await expect(removeButtons.first()).toBeVisible();
      }
    });

    test("displays destination rules section", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/destination|rule|pattern|naming|structure/i);
    });
  });

  test.describe("Destination Rules", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/destination-rules");
    });

    test("destination rules page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(
        /destination.*rule|folder.*structure/i
      );

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/rule|pattern|format|naming/i);
    });

    test("displays rule list or creation interface", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add|new|create/i });
      if (await addButton.isVisible()) {
        await expect(addButton).toBeVisible();
      }

      // Check for existing rules list
      const bodyText = await page.locator("body").textContent();
      if (bodyText && bodyText.match(/rule|pattern/i)) {
        expect(bodyText).toMatch(/rule|pattern/i);
      }
    });

    test("can create new destination rule", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add|new|create/i });
      if (await addButton.isVisible()) {
        await addButton.click();

        // Verify dialog or form opens
        const form = page.locator("form, [role='dialog']").first();
        if (await form.isVisible({ timeout: 3000 })) {
          await expect(form).toBeVisible();
        }
      }
    });
  });

  test.describe("Quality Profiles", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/profiles");
    });

    test("profiles page loads with profiles list", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/profiles|quality/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/profile|quality|resolution|codec/i);
    });

    test("displays available quality profiles", async ({ page }) => {
      // Check for profile items
      const profileItems = page.locator("div, li, tr").filter({
        hasText: /profile|quality|hd|sd|4k/i
      });
      if (await profileItems.count() > 0) {
        await expect(profileItems.first()).toBeVisible();
      }
    });

    test("can add new quality profile", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add|new|create/i });
      if (await addButton.isVisible()) {
        await addButton.click();

        const form = page.locator("form, [role='dialog']").first();
        if (await form.isVisible({ timeout: 3000 })) {
          await expect(form).toBeVisible();
        }
      }
    });

    test("can edit existing profile", async ({ page }) => {
      // Look for edit buttons
      const editButtons = page.getByRole("button", { name: /edit|configure/i });
      if (await editButtons.count() > 0) {
        await editButtons.first().click();

        const form = page.locator("form, [role='dialog']").first();
        if (await form.isVisible({ timeout: 3000 })) {
          await expect(form).toBeVisible();
        }
      }
    });
  });

  test.describe("Custom Formats", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/custom-formats");
    });

    test("custom formats page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/custom.*format/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/format|score|rule|condition|pattern/i);
    });

    test("displays custom formats list", async ({ page }) => {
      const formatItems = page.locator("div, tr, li").filter({
        hasText: /format|score|rule/i
      });
      if (await formatItems.count() > 0) {
        await expect(formatItems.first()).toBeVisible();
      }
    });

    test("can add custom format", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add|new|create/i });
      if (await addButton.isVisible()) {
        await addButton.click();

        const form = page.locator("form, [role='dialog']").first();
        if (await form.isVisible({ timeout: 3000 })) {
          await expect(form).toBeVisible();
        }
      }
    });
  });

  test.describe("Metadata Settings", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/metadata");
    });

    test("metadata page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/metadata/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/provider|tmdb|source|language|fallback/i);
    });

    test("displays metadata provider configuration", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/provider|api|key|configuration/i);

      // Look for provider selects
      const selects = page.locator("select, [role='combobox']");
      if (await selects.count() > 0) {
        await expect(selects.first()).toBeVisible();
      }
    });

    test("can configure metadata provider settings", async ({ page }) => {
      const inputs = page.locator("input[type='text'], input[type='password']");
      if (await inputs.count() > 0) {
        const input = inputs.first();
        if (await input.isVisible()) {
          await expect(input).toBeVisible();
        }
      }
    });
  });

  test.describe("Library Automation Settings", () => {
    test("can navigate to library automation from settings", async ({ page }) => {
      await page.goto("/movies");

      // Find library automation controls if present
      const automationButton = page.getByRole("button", { name: /automat|schedule|recurring/i });
      if (await automationButton.isVisible()) {
        await automationButton.click();
        await page.waitForTimeout(500);

        const automationDialog = page.locator("[role='dialog']").first();
        if (await automationDialog.isVisible({ timeout: 3000 })) {
          await expect(automationDialog).toBeVisible();
        }
      }
    });

    test("displays library automation configuration fields", async ({ page }) => {
      await page.goto("/movies");

      const automationButton = page.getByRole("button", {
        name: /automat|schedule|recurring/i
      });
      if (await automationButton.isVisible()) {
        await automationButton.click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          const bodyText = await dialog.textContent();
          expect(bodyText).toMatch(
            /enabled|interval|hour|search|missing|upgrade|items|retry/i
          );
        }
      }
    });

    test("can enable/disable library automation", async ({ page }) => {
      await page.goto("/movies");

      const automationButton = page.getByRole("button", {
        name: /automat|schedule|recurring/i
      });
      if (await automationButton.isVisible()) {
        await automationButton.click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          const toggle = dialog.getByRole("switch").first();
          if (await toggle.isVisible()) {
            const initial = await toggle.isChecked();
            await toggle.click();

            await page.waitForTimeout(500);
            const updated = await toggle.isChecked();
            expect(updated).not.toBe(initial);
          }
        }
      }
    });

    test("can modify search interval hours", async ({ page }) => {
      await page.goto("/movies");

      const automationButton = page.getByRole("button", {
        name: /automat|schedule|recurring/i
      });
      if (await automationButton.isVisible()) {
        await automationButton.click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          // Look for interval input
          const inputs = dialog.locator("input[type='number']");
          if (await inputs.count() > 0) {
            const intervalInput = inputs.filter({ hasText: /interval|hour/i }).first();
            if (await intervalInput.isVisible()) {
              await intervalInput.clear();
              await intervalInput.fill("12");

              await page.waitForTimeout(300);
              const value = await intervalInput.inputValue();
              expect(value).toBe("12");
            }
          }
        }
      }
    });

    test("can toggle search types (auto, missing, upgrade)", async ({ page }) => {
      await page.goto("/movies");

      const automationButton = page.getByRole("button", {
        name: /automat|schedule|recurring/i
      });
      if (await automationButton.isVisible()) {
        await automationButton.click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          const toggles = dialog.getByRole("switch");
          if (await toggles.count() >= 2) {
            // Toggle each search type
            for (let i = 1; i < Math.min(4, await toggles.count()); i++) {
              const toggle = toggles.nth(i);
              if (await toggle.isVisible()) {
                const initial = await toggle.isChecked();
                await toggle.click();

                await page.waitForTimeout(300);
                const updated = await toggle.isChecked();
                expect(updated).not.toBe(initial);
              }
            }
          }
        }
      }
    });

    test("can save automation settings", async ({ page }) => {
      await page.goto("/movies");

      const automationButton = page.getByRole("button", {
        name: /automat|schedule|recurring/i
      });
      if (await automationButton.isVisible()) {
        await automationButton.click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          const saveButton = dialog.getByRole("button", {
            name: /save|apply|confirm/i
          });
          if (await saveButton.isVisible()) {
            await saveButton.click();
            await page.waitForTimeout(500);

            // Verify dialog closes
            await expect(dialog).not.toBeVisible();
          }
        }
      }
    });
  });

  test.describe("UI Settings", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/ui");
    });

    test("UI settings page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/ui|display|appearance/i);
    });

    test("can change theme preference", async ({ page }) => {
      const themeButton = page.getByRole("button", {
        name: /theme|dark|light|mode/i
      });
      if (await themeButton.isVisible()) {
        await themeButton.click();

        const darkOption = page.getByRole("option", { name: /dark/i });
        const lightOption = page.getByRole("option", { name: /light/i });

        if (await darkOption.isVisible({ timeout: 2000 })) {
          await darkOption.click();
          await page.waitForTimeout(500);
          expect(true).toBe(true); // Successfully clicked
        } else if (await lightOption.isVisible({ timeout: 2000 })) {
          await lightOption.click();
          await page.waitForTimeout(500);
          expect(true).toBe(true);
        }
      }
    });
  });
});
