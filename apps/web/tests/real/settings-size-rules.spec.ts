import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Size Rules against the real server (measure 4, issue 1329). The tiers are the model the server holds; the
 * expectations are computed from what the server returns, and nothing here saves the model.
 */

interface Tier { name: string; rank: number; movieMinGb: number; movieMaxGb: number; episodeMinMb: number; episodeMaxMb: number }
const BANDS: Array<[string, (rank: number) => boolean]> = [
  ["Low quality", (r) => r < 10], ["SD", (r) => r >= 10 && r < 30], ["720p", (r) => r >= 30 && r < 60],
  ["1080p", (r) => r >= 60 && r < 95], ["2160p", (r) => r >= 95 && r < 125], ["Disc and raw", (r) => r >= 125]
];
const model = async (page: Page) => (await api<{ tiers: Tier[] }>(page, "GET", "/api/quality-model")).body;
const bandButton = (page: Page, label: string) => page.getByRole("button", { name: new RegExp(`^${label}`) });

test.describe("Size rules", () => {
  test.setTimeout(90_000);

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/quality");
    await untilQuiet(page);
  });

  test("the page shows the movie sizes in GB with the 1080p and 2160p bands open and every other band shut", async ({ page }) => {
    await expect(page.getByText("Movie file sizes")).toBeVisible();
    await expect(page.getByText("Min (GB)")).toBeVisible();
    await expect(page.getByText("Max (GB)")).toBeVisible();
    const present = BANDS.filter(([, test]) => (model(page), true) && test).map(([label]) => label);
    const tiers = (await model(page)).tiers;
    for (const [label, inBand] of BANDS) {
      if (!tiers.some((tier) => inBand(tier.rank))) continue;
      await expect(bandButton(page, label), label).toHaveAttribute("aria-expanded", label === "1080p" || label === "2160p" ? "true" : "false");
    }
  });

  test("the Movies and TV chips swap the table between GB and MB, and TV shows episode sizes", async ({ page }) => {
    await page.getByRole("radio", { name: "TV" }).or(page.getByRole("button", { name: "TV", exact: true })).first().click();
    await expect(page.getByText("Episode file sizes")).toBeVisible();
    await expect(page.getByText("Min (MB)")).toBeVisible();
    await expect(page.getByText("Movie file sizes")).toHaveCount(0);
    await page.getByRole("radio", { name: "Movies" }).or(page.getByRole("button", { name: "Movies", exact: true })).first().click();
    await expect(page.getByText("Movie file sizes")).toBeVisible();
    await expect(page.getByText("Min (GB)")).toBeVisible();
  });

  test("Expand all opens every band, Collapse all shuts them, a band's own heading toggles it, and a band's Min and Max come from its ruled tiers", async ({ page }) => {
    const tiers = (await model(page)).tiers;
    const present = BANDS.filter(([, inBand]) => tiers.some((tier) => inBand(tier.rank)));
    await page.getByRole("button", { name: "Expand all" }).click();
    for (const [label] of present) await expect(bandButton(page, label), label).toHaveAttribute("aria-expanded", "true");
    await page.getByRole("button", { name: "Collapse all" }).click();
    for (const [label] of present) await expect(bandButton(page, label), label).toHaveAttribute("aria-expanded", "false");
    await bandButton(page, "1080p").click();
    await expect(bandButton(page, "1080p")).toHaveAttribute("aria-expanded", "true");
    await expect(bandButton(page, "720p")).toHaveAttribute("aria-expanded", "false");

    for (const [label, inBand] of present) {
      const ruled = tiers.filter((tier) => inBand(tier.rank) && (tier.movieMinGb !== 0 || tier.movieMaxGb !== 0));
      const heading = bandButton(page, label);
      if (ruled.length === 0) {
        await expect(heading.locator("span", { hasText: /^—$/ })).toHaveCount(2);
        continue;
      }
      const min = Math.min(...ruled.map((tier) => tier.movieMinGb));
      const maxes = ruled.map((tier) => tier.movieMaxGb);
      const max = maxes.includes(0) ? 0 : Math.max(...maxes);
      await expect(heading).toContainText(`${min} GB`);
      await expect(heading).toContainText(max === 0 ? "Unlimited" : `${max} GB`);
    }
  });

  test("an edited size asks before leaving the page, Stay keeps the edit and Discard leaves it unsaved on the server", async ({ page }) => {
    const before = JSON.stringify((await model(page)).tiers);
    const input = page.getByRole("spinbutton").first();
    const original = await input.inputValue();
    await input.fill(String(Number(original) + 1));
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    const ask = page.getByRole("dialog", { name: "Unsaved changes" });
    await expect(ask).toBeVisible();
    await ask.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page).toHaveURL(/\/settings\/quality$/);
    await expect(input).toHaveValue(String(Number(original) + 1));
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    await ask.getByRole("button", { name: "Discard and continue", exact: true }).click();
    await expect(page).not.toHaveURL(/\/settings\/quality/);
    expect(JSON.stringify((await model(page)).tiers)).toBe(before);
  });
});
