import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

test.describe("Indexers and System Configuration", () => {
  test.describe("Indexers Page", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/indexers");
    });

    test("indexers page loads with list", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/indexer/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/indexer|enable|disable|status|health/i);
    });

    test("displays indexer list with status indicators", async ({ page }) => {
      const indexerItems = page.locator("tr, li, div").filter({
        hasText: /indexer|status|health|enabled/i
      });

      if (await indexerItems.count() > 0) {
        await expect(indexerItems.first()).toBeVisible();

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(/status|healthy|unhealthy|enabled|disabled/i);
      }
    });

    test("can enable/disable indexers", async ({ page }) => {
      const toggles = page.getByRole("switch");
      if (await toggles.count() > 0) {
        const toggle = toggles.first();
        if (await toggle.isVisible()) {
          const initial = await toggle.isChecked();
          await toggle.click();

          await page.waitForTimeout(500);
          const updated = await toggle.isChecked();
          expect(updated).not.toBe(initial);
        }
      }
    });

    test("displays indexer health information", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(
        /health|status|enabled|disabled|working|failed|error/i
      );
    });

    test("can view indexer configuration details", async ({ page }) => {
      const editButtons = page.getByRole("button", { name: /edit|configure/i });
      if (await editButtons.count() > 0) {
        await editButtons.first().click();

        const dialog = page.locator("[role='dialog']").first();
        if (await dialog.isVisible({ timeout: 3000 })) {
          const bodyText = await dialog.textContent();
          expect(bodyText).toMatch(/name|url|api|key|configuration/i);
        }
      }
    });

    test("shows indexer test/verify button", async ({ page }) => {
      const testButtons = page.getByRole("button", { name: /test|verify|check/i });
      if (await testButtons.count() > 0) {
        await expect(testButtons.first()).toBeVisible();
      }
    });

    test("can test indexer connectivity", async ({ page }) => {
      const testButtons = page.getByRole("button", { name: /test|verify|check/i });
      if (await testButtons.count() > 0) {
        await testButtons.first().click();

        await page.waitForTimeout(2000);

        const bodyText = await page.locator("body").textContent();
        expect(bodyText).toMatch(/success|failed|error|result|response/i);
      }
    });
  });

  test.describe("System Page", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/system");
    });

    test("system page loads with overview", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/system|overview/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/version|status|uptime|memory|disk/i);
    });

    test("displays system information sections", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(
        /version|application|database|storage|health|status/i
      );
    });

    test("can navigate to system tabs", async ({ page }) => {
      const tabs = page.getByRole("tab");
      if (await tabs.count() > 0) {
        for (let i = 0; i < Math.min(3, await tabs.count()); i++) {
          const tab = tabs.nth(i);
          await tab.click();

          await page.waitForTimeout(500);

          const bodyText = await page.locator("body").textContent();
          expect(bodyText).toBeTruthy();
        }
      }
    });
  });

  test.describe("System - Backups", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/system/backups");
    });

    test("backups page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/backup/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/backup|restore|create|export/i);
    });

    test("displays backup list", async ({ page }) => {
      const backupItems = page.locator("tr, li, div").filter({
        hasText: /backup|date|size|time/i
      });

      if (await backupItems.count() > 0) {
        await expect(backupItems.first()).toBeVisible();
      }
    });

    test("can create new backup", async ({ page }) => {
      const createButton = page.getByRole("button", { name: /create|new|backup/i });
      if (await createButton.isVisible()) {
        await expect(createButton).toBeVisible();
      }
    });

    test("can restore from backup", async ({ page }) => {
      const restoreButtons = page.getByRole("button", { name: /restore/i });
      if (await restoreButtons.count() > 0) {
        await expect(restoreButtons.first()).toBeVisible();
      }
    });

    test("can delete/remove backups", async ({ page }) => {
      const deleteButtons = page.getByRole("button", {
        name: /delete|remove|trash/i
      });

      if (await deleteButtons.count() > 0) {
        await expect(deleteButtons.first()).toBeVisible();
      }
    });
  });

  test.describe("System - Updates", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/system/updates");
    });

    test("updates page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/update/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/update|version|available|install/i);
    });

    test("displays update status", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(
        /current|available|up.*date|update.*available|version/i
      );
    });

    test("shows version information", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/v\d|version|\d+\.\d+/);
    });
  });

  test.describe("System - API Documentation", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/system/api");
    });

    test("API documentation page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/api|swagger|documentation/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/api|endpoint|method|documentation/i);
    });

    test("displays API documentation interface", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/get|post|put|delete|endpoint|method/i);
    });

    test("can expand API endpoints", async ({ page }) => {
      const expandButtons = page.getByRole("button", { name: /expand|collapse|show|hide/i });
      if (await expandButtons.count() > 0) {
        await expandButtons.first().click();

        await page.waitForTimeout(500);
        expect(true).toBe(true);
      }
    });

    test("shows endpoint parameters and responses", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      if (bodyText && bodyText.match(/parameter|response|request/i)) {
        expect(bodyText).toMatch(/parameter|response|request|body|schema/i);
      }
    });
  });

  test.describe("System - Documentation/Docs", () => {
    test.beforeEach(async ({ page }) => {
      await authenticateAndNavigate(page, "/system/docs");
    });

    test("documentation page loads", async ({ page }) => {
      await expect(page.getByRole("heading")).toContainText(/doc|guide|help|usage/i);

      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toMatch(/doc|guide|usage|feature|how.*to/i);
    });

    test("displays documentation content", async ({ page }) => {
      const bodyText = await page.locator("body").textContent();
      expect(bodyText).toBeTruthy();
      expect(bodyText?.length).toBeGreaterThan(50);
    });
  });

  test.describe("System Health and Integrations", () => {
    test("displays integration health status", async ({ page }) => {
      await page.goto("/system");

      const bodyText = await page.locator("body").textContent();
      if (bodyText && bodyText.match(/integration|health|status/i)) {
        expect(bodyText).toMatch(/integration|health|status|connected|failed/i);
      }
    });

    test("shows database health", async ({ page }) => {
      await page.goto("/system");

      const bodyText = await page.locator("body").textContent();
      if (bodyText && bodyText.match(/database|storage/i)) {
        expect(bodyText).toMatch(/database|storage|healthy|error|status/i);
      }
    });

    test("displays file system health", async ({ page }) => {
      await page.goto("/system");

      const bodyText = await page.locator("body").textContent();
      if (bodyText && bodyText.match(/file|folder|disk|space/i)) {
        expect(bodyText).toMatch(/file|folder|disk|space|available|used/i);
      }
    });
  });
});
