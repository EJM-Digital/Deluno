import { expect, test, type Page } from "@playwright/test";
import { openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Automation > Download health, the rule drawer's choices, against the real server with nothing stubbed (#1297): the Kind select offers the
 * seven kinds and the drawer's description follows the kind; the action select offers its three actions each with a one-line description;
 * closing a drawer with an edit in it asks first (Discard or keep editing) and a clean one closes at once.
 */

const KINDS = ["No progress", "Too slow", "Not enough peers", "Dead torrent", "Dangerous or junk file", "Client-reported failure", "Magnet stuck fetching metadata"];

async function openNewRule(page: Page) {
  await openScreen(page, "/search-cycles/download-health");
  await untilQuiet(page);
  await page.getByRole("button", { name: "New rule", exact: true }).first().click();
  return page.getByRole("dialog", { name: "New download health rule" });
}

test.describe("Download health rule drawer choices", () => {
  test("the Kind select offers the seven kinds and the drawer's description changes with the kind", async ({ page }) => {
    const drawer = await openNewRule(page);
    const kind = drawer.getByLabel("Kind", { exact: true });

    const offered = await kind.locator("option").allTextContents();
    expect(offered).toEqual(KINDS);

    await kind.selectOption({ label: "Dead torrent" });
    const first = (await drawer.locator("p").allTextContents()).join(" | ");
    await kind.selectOption({ label: "Too slow" });
    const second = (await drawer.locator("p").allTextContents()).join(" | ");
    expect(second).not.toBe(first);
  });

  test("the action select offers Just tell me, Retry elsewhere keep files and Replace it, each with its one-line description", async ({ page }) => {
    const drawer = await openNewRule(page);
    const action = drawer.getByLabel("When it strikes");

    expect(await action.locator("option").allTextContents()).toEqual(["Just tell me", "Retry elsewhere, keep files", "Replace it"]);
    await expect(drawer.getByText("Surface the finding. Deluno takes no action of its own.")).toBeVisible();
    await action.selectOption({ label: "Retry elsewhere, keep files" });
    await expect(drawer.getByText("Blocklist the release and search again, but leave it in the client untouched.")).toBeVisible();
    await action.selectOption({ label: "Replace it" });
    await expect(drawer.getByText("Remove it from the client, delete its files, blocklist the release, and search again.")).toBeVisible();
  });

  test("closing a drawer with an edit in it asks to discard the changes, keep editing leaves it open, and a clean drawer closes at once", async ({ page }) => {
    let drawer = await openNewRule(page);
    await drawer.getByLabel("Strikes before it acts").fill("7");
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    const ask = page.getByRole("alertdialog").or(page.getByRole("dialog", { name: "Discard unsaved changes?" }));
    await expect(ask.getByText("Your edits to this rule have not been saved.")).toBeVisible();
    await ask.getByRole("button", { name: "Keep editing" }).or(ask.getByRole("button", { name: "Cancel" })).first().click();
    await expect(drawer).toBeVisible();
    await expect(drawer.getByLabel("Strikes before it acts")).toHaveValue("7");
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await ask.getByRole("button", { name: "Discard", exact: true }).click();
    await expect(drawer).toHaveCount(0);

    drawer = await openNewRule(page);
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    await expect(page.getByText("Discard unsaved changes?")).toHaveCount(0);
  });

  test("the strikes field says in the person's own numbers when strike one lands and when the download is removed, and refuses a number outside 1 to 100", async ({ page }) => {
    const drawer = await openNewRule(page);
    const strikes = drawer.getByLabel("Strikes before it acts");

    await strikes.fill("1");
    await expect(drawer.getByText(/With 1 strike the download is removed at that same check\./)).toBeVisible();
    await strikes.fill("4");
    await expect(drawer.getByText(/with 4 strikes it is removed about \d+ min later/)).toBeVisible();
    await strikes.fill("101");
    await drawer.getByRole("button", { name: "Create rule", exact: true }).click();
    await expect(drawer.getByText(/1 and 100|between 1/i).first()).toBeVisible();
  });
});
