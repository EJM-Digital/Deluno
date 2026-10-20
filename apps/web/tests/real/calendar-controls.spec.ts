import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Calendar against the real server, nothing stubbed (measure 4, issue 1270): the range buttons move the window, Options opens
 * its drawer, and Subscribe makes the read-only calendar link that really serves the feed.
 */

const LINK_KEY = "Calendar subscription";

interface Key { id: string; name: string }

async function removeLinkKeys(page: Page) {
  const keys = (await api<Key[]>(page, "GET", "/api/api-keys")).body;
  for (const key of keys.filter((k) => k.name === LINK_KEY)) await api(page, "DELETE", `/api/api-keys/${key.id}`);
}

test.describe("Calendar", () => {
  test("Previous, Today and Next move the window and the Range choice changes its size", async ({ page }) => {
    await openScreen(page, "/calendar");
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    const heading = page.locator("main h2").first();
    const here = (await heading.innerText()).trim();
    await page.getByRole("button", { name: /^Next /i }).click();
    await expect(heading).not.toHaveText(here);
    await page.getByRole("button", { name: /^Previous /i }).click();
    await expect(heading).toHaveText(here);
    await page.getByRole("button", { name: /^Previous /i }).click();
    await expect(heading).not.toHaveText(here);
    await page.getByRole("button", { name: "Today", exact: true }).click();
    await expect(heading).toHaveText(here);

    await page.getByRole("tab", { name: "Week", exact: true }).click();
    await expect(page.getByRole("tab", { name: "Week", exact: true })).toHaveAttribute("aria-selected", "true");
    await expect(heading).not.toHaveText(here);
  });

  test("Options opens its drawer, a switch changes and Close shuts it", async ({ page }) => {
    await openScreen(page, "/calendar");
    await untilQuiet(page);
    await page.getByRole("button", { name: "Options", exact: true }).click();
    const drawer = page.getByRole("dialog", { name: "Calendar options" });
    await expect(drawer).toBeVisible();
    const toggle = drawer.getByRole("switch", { name: /Highlight titles below cutoff/ });
    const before = await toggle.isChecked();
    await toggle.click();
    await expect(toggle).toBeChecked({ checked: !before });
    await toggle.click();
    await expect(toggle).toBeChecked({ checked: before });
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);
  });

  test("Subscribe makes a read-only link that serves the feed, Copy copies it, and a second link replaces the first", async ({ page, context }) => {
    test.setTimeout(90_000);
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await openScreen(page, "/calendar");
    await removeLinkKeys(page);
    await untilQuiet(page);

    try {
      await page.getByRole("button", { name: "Subscribe", exact: true }).click();
      const drawer = page.getByRole("dialog", { name: "Subscribe to the schedule" });
      await expect(drawer.getByText("Create your link")).toBeVisible();

      await drawer.getByRole("switch", { name: /Include unmonitored titles/ }).click();
      await drawer.getByRole("textbox", { name: /Only these tags/ }).fill("Anime, Kids");
      const requests = recordRequests(page);
      await drawer.getByRole("button", { name: "Create calendar link", exact: true }).click();
      await expect(drawer.getByText("Your calendar link")).toBeVisible();
      requests.stop();

      const made = requests.find("POST", /^\/api\/api-keys$/);
      expect(made).toHaveLength(1);
      expect(made[0].body).toMatchObject({ name: LINK_KEY, scopes: "read" });

      const link = (await drawer.locator("code").first().innerText()).trim();
      const url = new URL(link);
      expect(url.pathname).toBe("/api/calendar/feed.ics");
      expect(url.searchParams.get("unmonitored")).toBe("true");
      expect(url.searchParams.get("tag")).toBe("Anime,Kids");
      const feed = await page.request.get(`${url.pathname}${url.search}`);
      expect(feed.status()).toBe(200);
      expect(await feed.text()).toContain("BEGIN:VCALENDAR");
      expect((await page.request.get("/api/calendar/feed.ics")).status(), "the feed is not open without the key").toBeGreaterThanOrEqual(400);

      await drawer.getByRole("button", { name: "Copy", exact: true }).click();
      await expect(page.getByText("Copied to clipboard")).toBeVisible();
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(link);

      // Open it again: the old link cannot be shown, and a new one replaces it.
      await page.keyboard.press("Escape");
      await expect(drawer).toHaveCount(0);
      await page.getByRole("button", { name: "Subscribe", exact: true }).click();
      await expect(page.getByRole("dialog", { name: "Subscribe to the schedule" }).getByText("You already have a link")).toBeVisible();
      await page.getByRole("button", { name: "Replace calendar link", exact: true }).click();
      await expect(page.getByText("Your calendar link")).toBeVisible();
      const keys = (await api<Key[]>(page, "GET", "/api/api-keys")).body.filter((k) => k.name === LINK_KEY);
      expect(keys, "one link at a time").toHaveLength(1);
      expect((await page.request.get(`${url.pathname}${url.search}`)).status(), "the first link stopped working").toBeGreaterThanOrEqual(400);
    } finally {
      await removeLinkKeys(page);
    }
  });
});
