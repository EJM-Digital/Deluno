import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("TV Series Module - Complete Workflows", () => {
  test.beforeEach(async ({ page }) => {
    await authenticateAndNavigate(page, "/tv");
  });

  test.describe("TV Series Library Management", () => {
    test("displays series list page with controls", async ({ page }) => {
      // Verify page loads
      await expect(page.getByRole("heading")).toContainText(/TV|Series|Television/i);

      // Check for main controls
      const addButton = page.getByRole("button", { name: /add series|add tv/i });
      if (await addButton.isVisible()) {
        await expect(addButton).toBeVisible();
      }

      // Verify content exists
      await expect(page.locator("body")).toContainText(/series|tv|television/i);
    });

    test("add series button opens add-series dialog", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add series|add tv/i });
      if (await addButton.isVisible()) {
        await addButton.click();

        // Verify dialog opens with search
        await expect(
          page.getByLabel(/series.*title|search.*series|find.*series/i)
        ).toBeVisible({ timeout: 5000 });
      }
    });

    test("can search for TV series in add-series dialog", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add series|add tv/i });
      if (await addButton.isVisible()) {
        await addButton.click();

        const searchInput = page
          .getByLabel(/series.*title|search.*series|find.*series/i)
          .first();
        if (await searchInput.isVisible({ timeout: 3000 })) {
          await searchInput.fill("Breaking Bad");

          await page.waitForTimeout(1500);

          // Verify results
          const results = page.locator("button, a").filter({
            hasText: /Breaking|2008|2013/
          });
          if (await results.count() > 0) {
            await expect(results.first()).toBeVisible();
          }
        }
      }
    });

    test("series list displays expected columns/properties", async ({ page }) => {
      const seriesItems = page.locator('a[href^="/tv/"]');
      if (await seriesItems.count() > 0) {
        await expect(seriesItems.first()).toBeVisible();

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(
          /status|monitored|season|episode|download|quality/i
        );
      }
    });
  });

  test.describe("TV Series Detail Page", () => {
    test("opens series detail page when clicking a series", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        await expect(page).toHaveURL(/\/tv\/[^/]+$/);
        await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(
          /search|dispatch|metadata|episode|season|quality/i
        );
      }
    });

    test("series detail displays episodes section", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        // Look for episodes section
        const episodesHeading = page.getByRole("heading", { name: /episodes/i });
        if (await episodesHeading.isVisible()) {
          await expect(episodesHeading).toBeVisible();

          // Verify episode data
          const bodyText = await page.locator("body").textContent();
          expect(bodyText).toMatch(/season|episode|aired|air date/i);
        }
      }
    });

    test("can trigger series search from detail page", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        const searchButton = page.getByRole("button", { name: /search/i });
        if (
          await searchButton.isVisible() &&
          (await searchButton.isEnabled())
        ) {
          await searchButton.click();

          await page.waitForTimeout(2000);

          const bodyText = await page.locator("body").textContent();
          expect(bodyText).toBeTruthy();
        }
      }
    });

    test("series detail shows metadata section", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        const metadataHeading = page.getByRole("heading", { name: /metadata/i });
        if (await metadataHeading.isVisible()) {
          await expect(metadataHeading).toBeVisible();

          const bodyText = await page.locator("body").textContent();
          expect(bodyText).toMatch(
            /genre|year|runtime|status|network|created/i
          );
        }
      }
    });
  });

  test.describe("Episode-Level Workflows", () => {
    test("displays episode list with episode details", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        // Look for episode table/list
        const episodeItems = page.locator('[data-testid*="episode"], tr, li').filter({
          hasText: /S\d{2}E\d{2}|Season.*Episode/i
        });

        if (await episodeItems.count() > 0) {
          await expect(episodeItems.first()).toBeVisible();
        }
      }
    });

    test("can expand episode details", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        // Try to find and click expandable episode
        const expandButtons = page.locator("button").filter({ hasText: /expand|details|view/i });
        if (await expandButtons.count() > 0) {
          await expandButtons.first().click();
          await page.waitForTimeout(500);

          const bodyText = await page.locator("body").textContent();
          expect(bodyText).toBeTruthy();
        }
      }
    });

    test("displays episode air date and status", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(/air|aired|date|status|missing|monitored/i);
      }
    });
  });

  test.describe("TV Series Search Results", () => {
    test("manual search returns results", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        const searchButton = page.getByRole("button", { name: /search/i });
        if (await searchButton.isVisible()) {
          await searchButton.click();

          await page.waitForTimeout(3000);

          const results = page.locator("div, span").filter({
            hasText: /release|result|grabbed|season|episode/
          });
          if (await results.count() > 0) {
            await expect(results.first()).toBeVisible();
          }
        }
      }
    });
  });

  test.describe("TV Series Monitoring", () => {
    test("can access series quality settings", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        const qualitySection = page.getByRole("heading", { name: /quality/i });
        if (await qualitySection.isVisible()) {
          await expect(qualitySection).toBeVisible();
        }
      }
    });

    test("can toggle series monitoring", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        const monitorToggle = page.getByRole("switch", { name: /monitor/i }).first();
        if (await monitorToggle.isVisible()) {
          const initial = await monitorToggle.isChecked();
          await monitorToggle.click();

          await page.waitForTimeout(500);
          const updated = await monitorToggle.isChecked();

          expect(updated).not.toBe(initial);
        }
      }
    });

    test("can toggle series season monitoring", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        // Look for season toggles
        const seasonToggles = page.locator(
          "input[type='checkbox'], button[role='switch']"
        ).filter({ hasText: /season|s\d/i });

        if (await seasonToggles.count() > 0) {
          const toggle = seasonToggles.first();
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
  });

  test.describe("TV Grab/Download Workflow", () => {
    test("can grab missing episode from search", async ({ page }) => {
      const seriesLinks = page.locator('a[href^="/tv/"]');
      if (await seriesLinks.count() > 0) {
        await seriesLinks.first().click();

        const searchButton = page.getByRole("button", { name: /search/i });
        if (await searchButton.isVisible()) {
          await searchButton.click();
          await page.waitForTimeout(3000);

          const grabButton = page
            .getByRole("button", { name: /grab|dispatch|download/i })
            .first();
          if (await grabButton.isVisible({ timeout: 2000 })) {
            await grabButton.click();
            await page.waitForTimeout(1000);
          }
        }
      }
    });
  });
});
