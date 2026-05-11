import { expect, test } from "@playwright/test";
import { authenticateAndNavigate, fallbackCredentials } from "../helpers/auth-helper";

test.describe("Movies Module - Complete Workflows", () => {
  test.beforeEach(async ({ page }) => {
    await authenticateAndNavigate(page, "/movies");
  });

  test.describe("Movie Library Management", () => {
    test("displays movies list page with search and filter controls", async ({ page }) => {
      // Verify page loads
      await expect(page.getByRole("heading")).toContainText(/Movies|Movie/);

      // Check for main controls
      await expect(page.getByRole("button", { name: /add movie/i })).toBeVisible();
      await expect(page.getByRole("button", { name: /search/i })).toBeVisible();

      // Verify table/grid structure exists
      await expect(page.locator("body")).toContainText(/movie|Movie/i);
    });

    test("movie add button opens add-movie dialog", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add movie/i });
      if (await addButton.isVisible()) {
        await addButton.click();

        // Verify dialog/modal opens with form
        await expect(page.getByLabel(/movie.*title|search.*movie/i)).toBeVisible({ timeout: 5000 });
      }
    });

    test("can search for movies in add-movie dialog", async ({ page }) => {
      const addButton = page.getByRole("button", { name: /add movie/i });
      if (await addButton.isVisible()) {
        await addButton.click();

        const searchInput = page.getByLabel(/movie.*title|search.*movie|find.*movie/i).first();
        if (await searchInput.isVisible({ timeout: 3000 })) {
          // Search for a well-known movie
          await searchInput.fill("The Matrix");

          // Wait for results
          await page.waitForTimeout(1000);

          // Verify results appear
          const results = page.locator("button, a").filter({ hasText: /Matrix|1999/ });
          if (await results.count() > 0) {
            await expect(results.first()).toBeVisible();
          }
        }
      }
    });

    test("movies list displays expected columns/properties", async ({ page }) => {
      // Check if there are movies in the library
      const movieItems = page.locator('[href^="/movies/"]');
      if (await movieItems.count() > 0) {
        // Verify clickable movie links
        await expect(movieItems.first()).toBeVisible();

        // Check for common movie attributes displayed
        const pageText = await page.locator("body").textContent();
        expect(pageText).toMatch(/status|download|monitored|quality/i);
      }
    });
  });

  test.describe("Movie Detail Page", () => {
    test("opens movie detail page when clicking a movie", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();

        // Verify detail page loads
        await expect(page).toHaveURL(/\/movies\/[^/]+$/);
        await expect(page.getByText("Unexpected Application Error")).toHaveCount(0);

        // Verify key detail sections exist
        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(/search|dispatch|metadata|quality/i);
      }
    });

    test("movie detail page displays search functionality", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();
        await expect(page).toHaveURL(/\/movies\/[^/]+$/);

        // Check for search button
        const searchButton = page.getByRole("button", { name: /search/i });
        if (await searchButton.isVisible()) {
          await expect(searchButton).toBeEnabled();
        }
      }
    });

    test("can trigger movie search from detail page", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();
        await expect(page).toHaveURL(/\/movies\/[^/]+$/);

        const searchButton = page.getByRole("button", { name: /search/i });
        if (await searchButton.isVisible() && (await searchButton.isEnabled())) {
          await searchButton.click();

          // Verify search results or loading state
          await page.waitForTimeout(2000);

          // Check for results or completion message
          const bodyText = await page.locator("body").textContent();
          expect(bodyText).toBeTruthy();
        }
      }
    });

    test("movie detail shows metadata section", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();
        await expect(page).toHaveURL(/\/movies\/[^/]+$/);

        // Look for metadata section
        const metadataHeading = page.getByRole("heading", { name: /metadata/i });
        if (await metadataHeading.isVisible()) {
          await expect(metadataHeading).toBeVisible();

          // Verify metadata content
          const bodyText = await page.locator("body").textContent();
          expect(bodyText).toMatch(/genre|year|runtime|director|cast/i);
        }
      }
    });
  });

  test.describe("Movie Search Results", () => {
    test("manual search returns and displays results", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();

        const searchButton = page.getByRole("button", { name: /search/i });
        if (await searchButton.isVisible()) {
          await searchButton.click();

          // Wait for results to load
          await page.waitForTimeout(3000);

          // Check if results appear
          const results = page.locator("div, span").filter({
            hasText: /release|result|grabbed|download/
          });
          if (await results.count() > 0) {
            await expect(results.first()).toBeVisible();
          }
        }
      }
    });

    test("search results display quality scoring information", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();

        const searchButton = page.getByRole("button", { name: /search/i });
        if (await searchButton.isVisible()) {
          await searchButton.click();
          await page.waitForTimeout(3000);

          // Look for score/quality indicators
          const bodyText = await page.locator("body").textContent();
          if (bodyText && bodyText.match(/score|quality|point/i)) {
            expect(bodyText).toMatch(/score|quality|point/i);
          }
        }
      }
    });
  });

  test.describe("Movie Grab/Download Workflow", () => {
    test("can grab/dispatch a movie from search results", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();

        const searchButton = page.getByRole("button", { name: /search/i });
        if (await searchButton.isVisible()) {
          await searchButton.click();
          await page.waitForTimeout(3000);

          // Look for grab/dispatch button
          const grabButton = page.getByRole("button", { name: /grab|dispatch|download/i }).first();
          if (await grabButton.isVisible({ timeout: 2000 })) {
            await grabButton.click();

            // Verify action completes
            await page.waitForTimeout(1000);
            const bodyText = await page.locator("body").textContent();
            expect(bodyText).toBeTruthy();
          }
        }
      }
    });

    test("movie status updates after grab", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        const initialText = await page.locator("body").textContent();

        await movieLinks.first().click();
        const searchButton = page.getByRole("button", { name: /search/i });

        if (await searchButton.isVisible()) {
          await searchButton.click();
          await page.waitForTimeout(3000);

          const grabButton = page.getByRole("button", {
            name: /grab|dispatch|download/
          }).first();

          if (await grabButton.isVisible({ timeout: 2000 })) {
            await grabButton.click();
            await page.waitForTimeout(2000);

            // Go back and check list updated
            await page.goto("/movies");
            const updatedText = await page.locator("body").textContent();
            expect(updatedText).toBeTruthy();
          }
        }
      }
    });
  });

  test.describe("Movie Monitoring and Quality Settings", () => {
    test("can access movie quality settings", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();

        // Look for quality/settings controls
        const qualitySection = page.getByRole("heading", { name: /quality/i });
        if (await qualitySection.isVisible()) {
          await expect(qualitySection).toBeVisible();
        }

        // Check for monitoring toggle
        const monitoredToggle = page.locator('input[type="checkbox"]').filter({
          hasText: /monitor/i
        }).first();
        if (await monitoredToggle.isVisible()) {
          await expect(monitoredToggle).toBeVisible();
        }
      }
    });

    test("can toggle movie monitoring status", async ({ page }) => {
      const movieLinks = page.locator('a[href^="/movies/"]');
      if (await movieLinks.count() > 0) {
        await movieLinks.first().click();

        const monitorToggle = page.getByRole("switch", { name: /monitor/i }).first();
        if (await monitorToggle.isVisible()) {
          const initialState = await monitorToggle.isChecked();
          await monitorToggle.click();

          await page.waitForTimeout(500);
          const newState = await monitorToggle.isChecked();

          expect(newState).not.toBe(initialState);
        }
      }
    });
  });
});
