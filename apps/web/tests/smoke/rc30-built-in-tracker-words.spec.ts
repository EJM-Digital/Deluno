import { expect, test, type Locator, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// rc.30 live test, #1137 and #1143. A person picks a built-in tracker in the New
// indexer drawer. What the drawer says about it should be in Deluno's words, not
// the words of another tool's definition file; and where it tells them about
// Cloudflare, the page it names should exist and be one click away.

async function pickTracker(page: Page, name: string): Promise<Locator> {
  await page.goto("/indexers/indexers");
  await page.getByRole("button", { name: "New indexer" }).first().click();
  const drawer = page.getByRole("dialog", { name: "New indexer" });
  await drawer.getByLabel("Protocol").selectOption("tracker");
  await drawer.getByRole("textbox", { name: "Search trackers" }).fill(name);
  await drawer.getByRole("listbox", { name: "Trackers" }).getByRole("option", { name: new RegExp(name, "i") }).first().click();
  return drawer;
}

test("a built-in tracker's settings read in Deluno's words (#1137)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The drawer is the same on a phone; the desktop run proves the words.");
  await authenticateAndNavigate(page, "/");
  const drawer = await pickTracker(page, "Nyaa");
  await expect(drawer.getByText("Nyaa", { exact: false }).first()).toBeVisible();

  // Deluno replaces those apps, so it names neither of them, anywhere in the drawer.
  const words = (await drawer.innerText()).replace(/\s+/g, " ");
  expect(words).not.toMatch(/Sonarr|Radarr/i);

  // The sort and order boxes show words a person picks, never the site's raw values.
  for (const select of await drawer.locator("select").all()) {
    const shown = await select.evaluate((element) => (element as HTMLSelectElement).selectedOptions[0]?.textContent ?? "");
    expect(shown, "a setting shows its raw value").not.toMatch(/^(created|desc|asc|id|size|seeders|downloads|comments)$/);
  }
  for (const input of await drawer.getByRole("textbox").all()) {
    expect(await input.inputValue(), "a setting box shows a raw value").not.toMatch(/^(created|desc|asc)$/);
  }
  await page.keyboard.press("Escape");
});

test("the Cloudflare note on a built-in tracker names a page that opens, one click away (#1143)", async ({ page, isMobile }) => {
  test.skip(isMobile, "The drawer is the same on a phone; the desktop run proves the link.");
  await authenticateAndNavigate(page, "/");
  const drawer = await pickTracker(page, "1337x");
  const note = drawer.getByText(/Cloudflare/).first();
  await expect(note).toBeVisible();
  // It sends the person to Preferences, General, and that is not a page that does not exist.
  expect((await drawer.innerText())).not.toMatch(/Settings,? General/);
  await drawer.getByRole("link", { name: /Preferences, General/ }).first().click();
  // The drawer holds a draft, so leaving asks first.
  await page.getByRole("button", { name: "Discard and continue" }).click();
  await expect(page).toHaveURL(/\/settings\/general/);
  await expect(page.getByText("Cloudflare helper").first()).toBeVisible();
  await expect(page.getByRole("textbox", { name: /Helper address/ })).toBeVisible();
});
