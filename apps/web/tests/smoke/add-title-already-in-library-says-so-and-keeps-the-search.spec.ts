import { readFileSync } from "node:fs";
import path from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1507. In Add movie / Add TV show a search result already in the library says "In your library" in words, cannot be picked, and has
// its own Open button; pressing Open leaves for the title and Back returns to the same search, matches and picks. The search answers are
// the gateway's recorded TMDb replies (tests/fixtures/recorded/tmdb), with the first result marked as held the way the server marks it.

const recorded = (state: string) => JSON.parse(readFileSync(path.resolve(import.meta.dirname, `../../../../tests/fixtures/recorded/tmdb/2026-11-04/${state}.json`), "utf8")).exchanges[0].reply.body.results;

async function heldTitleSaysSoAndOpenKeepsTheSearch(page: Page, { route, dialogName, state, search, detail }: { route: string; dialogName: string; state: string; search: string; detail: RegExp }) {
  await authenticateAndNavigate(page, route);
  await page.route("**/api/metadata/search?**", async (routeCall) => {
    const results = recorded(state);
    results[0] = { ...results[0], libraryEntryId: "held-entry" };
    await routeCall.fulfill({ json: results });
  });

  await page.goto(`${route}?add=true`);
  const dialog = page.getByRole("dialog", { name: dialogName });
  await expect(dialog).toBeVisible();
  const box = dialog.getByRole("textbox", { name: "What do you want to add?" });
  await box.fill(search);
  await box.press("Enter");

  const held = dialog.locator("[data-held]");
  await expect(held).toHaveCount(1);
  await expect(held).toContainText("In your library");
  // Not pickable: it is no button, and pressing it changes nothing.
  expect(await held.evaluate((el) => el.tagName)).not.toBe("BUTTON");
  await held.locator("span.line-clamp-2").first().click();
  await expect(dialog.getByText("Selected", { exact: true })).toHaveCount(0);

  // Pick another result first, so the picks are shown to survive too.
  const pickable = dialog.locator("[data-testid='add-matches'] button[title^='Select ']").first();
  const pickedTitle = (await pickable.getAttribute("title"))!.replace("Select ", "");
  await pickable.click();
  await expect(dialog.getByText("Selected", { exact: true })).toBeVisible();
  const typed = await box.inputValue();

  await held.getByRole("button", { name: /^Open .*already in your library$/ }).click();
  await expect(page).toHaveURL(detail);

  await page.goBack();
  const back = page.getByRole("dialog", { name: dialogName });
  await expect(back).toBeVisible();
  await expect(back.getByRole("textbox", { name: "What do you want to add?" })).toHaveValue(typed);
  await expect(back.locator("[data-held]")).toHaveCount(1);
  await expect(back.getByTitle(`Select ${pickedTitle}`)).toContainText("Selected");
}

test("movies: a title already in the library says so, cannot be picked, and Open keeps the add screen's search (#1507)", async ({ page }) => {
  test.setTimeout(90_000);
  await heldTitleSaysSoAndOpenKeepsTheSearch(page, { route: "/movies", dialogName: "Add movie", state: "search-movie-kickboxer-page1", search: "Kickboxer", detail: /[/]movies[/]held-entry/ });
});

test("TV: a title already in the library says so, cannot be picked, and Open keeps the add screen's search (#1507)", async ({ page }) => {
  test.setTimeout(90_000);
  await heldTitleSaysSoAndOpenKeepsTheSearch(page, { route: "/tv", dialogName: "Add TV show", state: "search-tv-love-page1", search: "Love", detail: /[/]tv[/]held-entry/ });
});
