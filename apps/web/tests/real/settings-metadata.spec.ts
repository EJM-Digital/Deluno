import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Metadata against the real server, nothing stubbed (measure 4, issue 1329): leaving the page with an unsaved edit asks. Each test puts back what it changed.
 */

interface Settings { metadataLanguage: string | null; metadataCertificationCountry: string | null }
const settings = async (page: Page) => (await api<Settings>(page, "GET", "/api/settings")).body;
const putBack = (page: Page, s: Settings) => api(page, "PATCH" as "POST", "/api/settings", { metadataLanguage: s.metadataLanguage ?? "en", metadataCertificationCountry: s.metadataCertificationCountry ?? "US" });

test.describe("Metadata settings", () => {
  test.setTimeout(90_000);
  let before: Settings;

  test.beforeEach(async ({ page }) => {
    await openScreen(page, "/settings/metadata");
    before = await settings(page);
    await untilQuiet(page);
  });

  test.afterEach(async ({ page }) => { await putBack(page, before); });

  test("leaving with an unsaved language asks; Cancel stays, Discard and continue keeps the stored language, Save and continue stores it", async ({ page }) => {
    const prompt = page.getByRole("dialog", { name: "Unsaved changes" });
    const next = before.metadataLanguage === "ja" ? "de" : "ja";
    await page.getByLabel("Details language").selectOption(next);
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    await expect(prompt).toBeVisible();
    await prompt.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page).toHaveURL(/\/settings\/metadata/);
    await expect(page.getByLabel("Details language")).toHaveValue(next);
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    await prompt.getByRole("button", { name: "Discard and continue", exact: true }).click();
    await expect(page).not.toHaveURL(/settings\/metadata/);
    expect((await settings(page)).metadataLanguage).toBe(before.metadataLanguage);

    await openScreen(page, "/settings/metadata");
    await untilQuiet(page);
    await page.getByLabel("Details language").selectOption(next);
    await page.getByRole("link", { name: "Dashboard" }).first().click();
    await prompt.getByRole("button", { name: "Save and continue", exact: true }).click();
    await expect(page).not.toHaveURL(/settings\/metadata/);
    await expect.poll(async () => (await settings(page)).metadataLanguage).toBe(next);
  });

  test("a language or region that is not a known code is refused with a sentence on its own field, and a known one saves", async ({ page }) => {
    await page.getByLabel("Details language").selectOption({ label: "Other language code" });
    await page.getByRole("textbox", { name: "Other language code" }).fill("english");
    await page.getByRole("button", { name: "Save metadata settings", exact: true }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Details language must be a language code such as en, de or pt-BR." })).toBeVisible();
    expect((await settings(page)).metadataLanguage).toBe(before.metadataLanguage);

    await page.getByRole("textbox", { name: "Other language code" }).fill("pt-BR");
    await page.getByLabel("Ratings region").selectOption({ label: "Other country code" });
    await page.getByRole("textbox", { name: "Other country code" }).fill("Netherlands");
    await page.getByRole("button", { name: "Save metadata settings", exact: true }).click();
    await expect(page.getByRole("alert").filter({ hasText: "Ratings region must be a two-letter country code such as US, GB or NL." })).toBeVisible();
    expect((await settings(page)).metadataCertificationCountry).toBe(before.metadataCertificationCountry);

    await page.getByRole("textbox", { name: "Other country code" }).fill("NL");
    await page.getByRole("button", { name: "Save metadata settings", exact: true }).click();
    await expect.poll(async () => (await settings(page)).metadataLanguage).toBe("pt-BR");
    expect((await settings(page)).metadataCertificationCountry).toBe("NL");
  });
});
