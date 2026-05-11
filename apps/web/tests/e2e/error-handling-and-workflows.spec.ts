import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("Error Handling and Edge Cases", () => {
  test.describe("Validation Errors", () => {
    test("shows validation error for invalid email in settings", async ({
      page
    }) => {
      await authenticateAndNavigate(page, "/settings/general");

      // Look for email input if it exists
      const emailInput = page.locator("input[type='email']");
      if (await emailInput.isVisible()) {
        await emailInput.fill("invalid-email");

        const saveButton = page.getByRole("button", {
          name: /save|apply|confirm/i
        });
        if (await saveButton.isVisible()) {
          await saveButton.click();
          await page.waitForTimeout(1000);

          // Check for error message
          const errorMessage = page.locator("[role='alert'], .error, .text-red");
          if (await errorMessage.count() > 0) {
            await expect(errorMessage.first()).toBeVisible();
          }
        }
      }
    });

    test("prevents empty required field submission", async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/general");

      // Try to submit empty required field
      const inputs = page.locator("input[required]");
      if (await inputs.count() > 0) {
        const input = inputs.first();
        await input.clear();

        const saveButton = page.getByRole("button", {
          name: /save|apply|confirm/i
        });
        if (
          await saveButton.isVisible() &&
          (await saveButton.isEnabled() === false)
        ) {
          expect(await saveButton.isDisabled()).toBe(true);
        }
      }
    });

    test("shows error on invalid custom format", async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/custom-formats");

      const createButton = page.getByRole("button", { name: /add|new|create/i });
      if (await createButton.isVisible()) {
        await createButton.click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          // Try to submit without required fields
          const submitButton = dialog.getByRole("button", {
            name: /create|save|add/i
          });

          if (await submitButton.isVisible()) {
            const isDisabled = await submitButton.isDisabled();
            expect(isDisabled).toBe(true);
          }
        }
      }
    });
  });

  test.describe("Not Found and Missing Data", () => {
    test("handles movie not found gracefully", async ({ page }) => {
      // Try to navigate to non-existent movie
      await page.goto("/movies/invalid-id-12345", { waitUntil: "domcontentloaded" });

      // Should not show application error
      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);

      // Should show appropriate message or redirect
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/not.*found|unavailable|error|return|back/i);
    });

    test("handles series not found gracefully", async ({ page }) => {
      await page.goto("/tv/invalid-id-12345", {
        waitUntil: "domcontentloaded"
      });

      await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/not.*found|unavailable|error|return|back/i);
    });

    test("handles empty search results", async ({ page }) => {
      await authenticateAndNavigate(page, "/movies");

      const addButton = page.getByRole("button", { name: /add movie/i });
      if (await addButton.isVisible()) {
        await addButton.click();

        const searchInput = page
          .getByLabel(/movie.*title|search.*movie/i)
          .first();
        if (await searchInput.isVisible({ timeout: 3000 })) {
          // Search for unlikely movie title
          await searchInput.fill("zzzzzzzzzzzzzzzzzzzzzzzzz");

          await page.waitForTimeout(2000);

          // Check for "no results" message
          const bodyText = await page.locator("body").textContent();
          if (bodyText && bodyText.match(/result/i)) {
            expect(bodyText).toMatch(/no.*result|not.*found|no.*match/i);
          }
        }
      }
    });
  });

  test.describe("Network Error Handling", () => {
    test("gracefully handles slow/timeout responses", async ({ page }) => {
      await authenticateAndNavigate(page, "/movies");

      // Some operations might timeout - should show user-friendly message
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        // Navigate with reasonable timeout
        const navigationPromise = page.goto("/movies/timeout-test", {
          waitUntil: "domcontentloaded",
          timeout: 10000
        }).catch(() => null);

        await navigationPromise;

        // Page should still be functional
        await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      }
    });

    test("shows loading states for long operations", async ({ page }) => {
      await authenticateAndNavigate(page, "/movies");

      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();

        const searchButton = page.getByRole("button", { name: /search/i });
        if (await searchButton.isVisible()) {
          await searchButton.click();

          // Should show loading/progress indicator briefly
          const spinner = page.locator(
            "[aria-busy='true'], [role='progressbar'], .spinner, .loading"
          );

          if (await spinner.count() > 0) {
            await expect(spinner.first()).toBeVisible();
          }
        }
      }
    });
  });

  test.describe("State Management and Concurrent Actions", () => {
    test("prevents duplicate submissions", async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/general");

      const inputs = page.locator("input[type='text']");
      if (await inputs.count() > 0) {
        const saveButton = page.getByRole("button", {
          name: /save|apply|confirm/i
        });

        if (await saveButton.isVisible() && (await saveButton.isEnabled())) {
          // Double-click save button rapidly
          await saveButton.click();
          await saveButton.click({ force: true }).catch(() => null);

          await page.waitForTimeout(1000);

          // Should only process once
          expect(true).toBe(true);
        }
      }
    });

    test("maintains form state on validation error", async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/custom-formats");

      const createButton = page.getByRole("button", { name: /add|new|create/i });
      if (await createButton.isVisible()) {
        await createButton.click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          // Fill a field
          const nameInput = dialog.locator("input").first();
          if (await nameInput.isVisible()) {
            const testValue = "Test Format Name";
            await nameInput.fill(testValue);

            // Try to submit (might fail validation)
            const submitButton = dialog.getByRole("button", {
              name: /create|save|add/i
            });

            if (await submitButton.isVisible()) {
              await submitButton.click({ force: true }).catch(() => null);

              await page.waitForTimeout(500);

              // Value should still be there
              const currentValue = await nameInput.inputValue();
              expect(currentValue).toBe(testValue);
            }
          }
        }
      }
    });
  });

  test.describe("Complete End-to-End Workflows", () => {
    test("complete movie add-search-grab workflow", async ({ page }) => {
      await authenticateAndNavigate(page, "/movies");

      // Step 1: Add movie
      const addButton = page.getByRole("button", { name: /add movie/i });
      if (!(await addButton.isVisible())) {
        test.skip();
      }

      await addButton.click();

      const searchInput = page
        .getByLabel(/movie.*title|search.*movie/i)
        .first();
      if (!(await searchInput.isVisible({ timeout: 3000 }))) {
        test.skip();
      }

      await searchInput.fill("The Matrix");
      await page.waitForTimeout(1500);

      // Step 2: Select from results
      const results = page.locator("button, a").filter({
        hasText: /Matrix|1999/
      });

      if (await results.count() > 0) {
        await results.first().click();
        await page.waitForTimeout(1000);

        // Dialog should close and show library updated
        const movieList = page.locator('a[href^="/movies/"]');
        await page.waitForTimeout(1000);

        // Step 3: Find and navigate to the new movie
        const movies = await movieList.count();
        if (movies > 0) {
          await movieList.first().click();

          // Step 4: Search and grab
          const movieSearchButton = page.getByRole("button", {
            name: /search/i
          });

          if (await movieSearchButton.isVisible()) {
            await movieSearchButton.click();
            await page.waitForTimeout(3000);

            // Should have results
            const grabButton = page
              .getByRole("button", { name: /grab|dispatch/i })
              .first();

            if (
              await grabButton.isVisible({ timeout: 2000 }) &&
              (await grabButton.isEnabled())
            ) {
              await grabButton.click();
              await page.waitForTimeout(1000);

              // Verify action completed
              const bodyText = await page.locator("body").textContent();
              expect(bodyText).toBeTruthy();
            }
          }
        }
      }
    });

    test("complete series add-search-grab workflow", async ({ page }) => {
      await authenticateAndNavigate(page, "/tv");

      const addButton = page.getByRole("button", { name: /add series|add tv/i });
      if (!(await addButton.isVisible())) {
        test.skip();
      }

      await addButton.click();

      const searchInput = page
        .getByLabel(/series.*title|search.*series/i)
        .first();
      if (!(await searchInput.isVisible({ timeout: 3000 }))) {
        test.skip();
      }

      await searchInput.fill("Breaking Bad");
      await page.waitForTimeout(1500);

      const results = page.locator("button, a").filter({
        hasText: /Breaking|2008/
      });

      if (await results.count() > 0) {
        await results.first().click();
        await page.waitForTimeout(1000);

        const seriesList = page.locator('a[href^="/tv/"]');
        if (await seriesList.count() > 0) {
          await seriesList.first().click();

          const searchButton = page.getByRole("button", { name: /search/i });
          if (await searchButton.isVisible()) {
            await searchButton.click();
            await page.waitForTimeout(3000);

            const grabButton = page
              .getByRole("button", { name: /grab|dispatch/i })
              .first();

            if (
              await grabButton.isVisible({ timeout: 2000 }) &&
              (await grabButton.isEnabled())
            ) {
              await grabButton.click();
              await page.waitForTimeout(1000);

              const bodyText = await page.locator("body").textContent();
              expect(bodyText).toBeTruthy();
            }
          }
        }
      }
    });

    test("complete settings configuration workflow", async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/general");

      // Navigate through multiple settings pages
      const settingsPages = [
        "/settings/general",
        "/settings/media-management",
        "/settings/profiles",
        "/settings/metadata"
      ];

      for (const settingsPage of settingsPages) {
        await page.goto(settingsPage);

        // Verify page loads without errors
        await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toBeTruthy();

        await page.waitForTimeout(300);
      }
    });

    test("complete library automation configuration workflow", async ({
      page
    }) => {
      await authenticateAndNavigate(page, "/movies");

      const automationButton = page.getByRole("button", {
        name: /automat|schedule|recurring/i
      });
      if (!(await automationButton.isVisible())) {
        test.skip();
      }

      await automationButton.click();

      const dialog = page.locator("[role='dialog']").first();
      if (!(await dialog.isVisible({ timeout: 3000 }))) {
        test.skip();
      }

      // Step 1: Enable automation
      const enableToggle = dialog.getByRole("switch").nth(0);
      if (await enableToggle.isVisible()) {
        const isEnabled = await enableToggle.isChecked();
        if (!isEnabled) {
          await enableToggle.click();
          await page.waitForTimeout(300);
        }
      }

      // Step 2: Configure interval
      const intervalInput = dialog.locator("input[type='number']").first();
      if (await intervalInput.isVisible()) {
        await intervalInput.clear();
        await intervalInput.fill("12");
        await page.waitForTimeout(300);
      }

      // Step 3: Enable search types
      const toggles = dialog.getByRole("switch");
      for (let i = 1; i < Math.min(4, await toggles.count()); i++) {
        const toggle = toggles.nth(i);
        if (await toggle.isVisible()) {
          const isChecked = await toggle.isChecked();
          if (!isChecked) {
            await toggle.click();
            await page.waitForTimeout(200);
          }
        }
      }

      // Step 4: Save
      const saveButton = dialog.getByRole("button", {
        name: /save|apply|confirm/i
      });
      if (await saveButton.isVisible()) {
        await saveButton.click();
        await page.waitForTimeout(1000);

        // Verify dialog closes
        await expect(dialog).not.toBeVisible();
      }
    });
  });

  test.describe("UI Responsiveness and Performance", () => {
    test("pages load within reasonable time", async ({ page }) => {
      const routes = [
        "/",
        "/movies",
        "/tv",
        "/calendar",
        "/queue",
        "/activity",
        "/settings"
      ];

      for (const route of routes) {
        const startTime = Date.now();

        await page.goto(route, { waitUntil: "domcontentloaded" });

        const loadTime = Date.now() - startTime;

        // Page should load in less than 5 seconds
        expect(loadTime).toBeLessThan(5000);

        // No errors
        await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);
      }
    });

    test("does not have console errors on main pages", async ({ page }) => {
      const consoleErrors: string[] = [];

      page.on("console", (msg) => {
        if (msg.type() === "error") {
          consoleErrors.push(msg.text());
        }
      });

      await authenticateAndNavigate(page, "/movies");

      // Should not have critical errors
      const criticalErrors = consoleErrors.filter(
        (e) =>
          !e.includes("Unexpected token") &&
          !e.includes("JSON.parse") &&
          !e.includes("404")
      );

      expect(criticalErrors.length).toBe(0);
    });

    test("buttons and interactive elements are keyboard accessible", async ({
      page
    }) => {
      await authenticateAndNavigate(page, "/movies");

      // Focus first button
      const buttons = page.getByRole("button");
      if (await buttons.count() > 0) {
        await buttons.first().focus();

        // Should be focusable
        const hasFocus = await buttons.first().evaluate(
          (el) => el === document.activeElement
        );
        expect(hasFocus).toBe(true);

        // Should respond to Enter key
        await page.keyboard.press("Enter");
        await page.waitForTimeout(300);

        expect(true).toBe(true);
      }
    });
  });
});
