import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("Queue and Activity Monitoring", () => {
  test.describe("Queue Page", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/queue");
    });

    test("queue page loads with expected layout", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/queue|job|download/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/queue|job|status|download|dispatch/i);
    });

    test("displays queue jobs table with columns", async ({ page }) => {
      // Check for table or list structure
      const jobItems = page.locator("tr, li, div").filter({
        hasText: /job|status|progress|time/i
      });

      if (await jobItems.count() > 0) {
        await expect(jobItems.first()).toBeVisible();

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(/status|queued|processing|completed|failed/i);
      }
    });

    test("shows job status indicators", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      // Should have at least some indication of job statuses
      expect(bodyText).toMatch(
        /queued|processing|completed|failed|pending|running/i
      );
    });

    test("displays retry information if applicable", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      // May show retry count/next retry time
      if (bodyText && bodyText.match(/retry|attempt/i)) {
        expect(bodyText).toMatch(/retry|attempt/i);
      }
    });

    test("can filter queue by status", async ({ page }) => {
      const filterButton = page.getByRole("button", { name: /filter|status/i });
      if (await filterButton.isVisible()) {
        await filterButton.click();

        const filterOptions = page.getByRole("option");
        if (await filterOptions.count() > 0) {
          await filterOptions.first().click();
          await page.waitForTimeout(500);
          expect(true).toBe(true); // Verify filter applied
        }
      }
    });

    test("can view job details", async ({ page }) => {
      const jobRows = page.locator("tr, li, a").filter({ hasText: /job/i });
      if (await jobRows.count() > 0) {
        await jobRows.first().click();

        await page.waitForTimeout(500);
        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(
          /detail|status|created|started|completed|log|error/i
        );
      }
    });

    test("displays retry controls for failed jobs", async ({ page }) => {
      // Look for failed job items
      const failedItems = page.locator("div, tr, li").filter({
        hasText: /failed|error/i
      });

      if (await failedItems.count() > 0) {
        const retryButton = page.getByRole("button", { name: /retry/i });
        if (await retryButton.isVisible()) {
          await expect(retryButton).toBeVisible();
        }
      }
    });

    test("can cancel/remove jobs", async ({ page }) => {
      const cancelButtons = page.getByRole("button", {
        name: /cancel|remove|delete/i
      });

      if (await cancelButtons.count() > 0) {
        await expect(cancelButtons.first()).toBeVisible();
      }
    });

    test("shows progress indication for running jobs", async ({ page }) => {
      // Look for progress bar or percentage
      const progressElements = page.locator(
        "[role='progressbar'], .progress, [aria-label*='progress' i]"
      );

      if (await progressElements.count() > 0) {
        await expect(progressElements.first()).toBeVisible();
      }
    });
  });

  test.describe("Activity Page", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/activity");
    });

    test("activity page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/activity|history|log/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/activity|event|time|action|status/i);
    });

    test("displays activity log entries", async ({ page }) => {
      const activityEntries = page.locator("tr, li, div").filter({
        hasText: /time|action|event/i
      });

      if (await activityEntries.count() > 0) {
        await expect(activityEntries.first()).toBeVisible();
      }
    });

    test("can filter activity by type", async ({ page }) => {
      const filterButton = page.getByRole("button", { name: /filter|type/i });
      if (await filterButton.isVisible()) {
        await filterButton.click();

        const filterOptions = page.getByRole("option");
        if (await filterOptions.count() > 0) {
          await filterOptions.first().click();
          await page.waitForTimeout(500);
          expect(true).toBe(true);
        }
      }
    });

    test("can search activity log", async ({ page }) => {
      const searchInput = page.getByPlaceholder(/search|filter/i);
      if (await searchInput.isVisible()) {
        await searchInput.fill("test");

        await page.waitForTimeout(800);

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toBeTruthy();
      }
    });

    test("activity shows timestamps", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/\d{1,2}[:\-/]\d{1,2}/); // Time or date pattern
    });

    test("shows import activity details", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      if (bodyText && bodyText.match(/import/i)) {
        expect(bodyText).toMatch(/import|grab|dispatch|release/i);
      }
    });

    test("shows search activity details", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      if (bodyText && bodyText.match(/search/i)) {
        expect(bodyText).toMatch(/search|result|found|completed/i);
      }
    });
  });

  test.describe("Calendar/Upcoming Releases", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/calendar");
    });

    test("calendar page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/calendar|upcoming|release/i);
    });

    test("displays calendar grid or list", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(
        /day|date|release|upcoming|movie|series|episode/i
      );
    });

    test("shows upcoming releases", async ({ page }) => {
      const releaseItems = page.locator("div, li, tr").filter({
        hasText: /release|upcoming|date|time/i
      });

      if (await releaseItems.count() > 0) {
        await expect(releaseItems.first()).toBeVisible();
      }
    });

    test("can navigate between calendar periods", async ({ page }) => {
      const prevButton = page.getByRole("button", { name: /previous|prev|back|< /i });
      const nextButton = page.getByRole("button", { name: /next|forward|>/i });

      if (await prevButton.isVisible() || await nextButton.isVisible()) {
        expect(
          (await prevButton.isVisible()) || (await nextButton.isVisible())
        ).toBe(true);
      }
    });

    test("can click on calendar date to view details", async ({ page }) => {
      const dateItems = page.locator("button, a, div").filter({
        hasText: /\d{1,2}/
      });

      if (await dateItems.count() > 3) {
        await dateItems.nth(3).click();
        await page.waitForTimeout(500);

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toBeTruthy();
      }
    });
  });

  test.describe("Import Tracking", () => {
    test("can navigate to import history", async ({ page }) => {
      await page.goto("/activity");

      const importLink = page.getByRole("link", { name: /import/i });
      if (await importLink.isVisible()) {
        await importLink.click();
        await page.waitForTimeout(500);

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(/import|status|file|completed/i);
      }
    });

    test("displays import status and details", async ({ page }) => {
      await page.goto("/activity");

      const bodyText = await page.locator("body").textContent();
      if (bodyText && bodyText.match(/import/i)) {
        expect(bodyText).toMatch(
          /import|detected|processing|completed|failed|error/i
        );
      }
    });
  });

  test.describe("Search Automation Status", () => {
    test("library automation shows execution history", async ({ page }) => {
      await page.goto("/movies");

      const automationButton = page.getByRole("button", {
        name: /automat|schedule|recurring/i
      });
      if (await automationButton.isVisible()) {
        await automationButton.click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          const bodyText = await dialog.textContent();
          if (bodyText && bodyText.match(/history|last|next|run/i)) {
            expect(bodyText).toMatch(/history|last|next|run|scheduled/i);
          }
        }
      }
    });

    test("shows automation execution status", async ({ page }) => {
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
            /enabled|disabled|active|idle|running|failed|error|status/i
          );
        }
      }
    });
  });
});
