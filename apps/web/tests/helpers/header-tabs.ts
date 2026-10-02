import { expect, type Locator, type Page } from "@playwright/test";

/**
 * Tabs live on the header's title line from `lg` up, with the ones that do not fit folded into a "More" menu, and in a
 * scrolling row of their own below it (#1113). A test should not care which: these find a tab wherever it is.
 */

function sectionLink(page: Page, name: string): Locator {
  return page.getByRole("navigation", { name: "Sections" }).getByRole("link", { name, exact: true });
}

/** Opens a route tab: its link when drawn, otherwise through the More menu. */
export async function openTab(page: Page, name: string): Promise<void> {
  try {
    await sectionLink(page, name).first().click({ timeout: 2_000 });
    return;
  } catch {
    // Folded into More, or folded while it was being clicked.
  }
  await page.getByRole("button", { name: "More", exact: true }).click();
  await page.getByRole("menuitem", { name, exact: true }).click();
}

/** The tab is reachable: drawn in the row, or listed under More. */
export async function expectTabReachable(page: Page, name: string): Promise<void> {
  const link = sectionLink(page, name);
  await expect(link.or(page.getByRole("button", { name: "More", exact: true }))).not.toHaveCount(0);
  if (await link.count()) return;
  await page.getByRole("button", { name: "More", exact: true }).click();
  await expect(page.getByRole("menuitem", { name, exact: true })).toBeVisible();
  await page.keyboard.press("Escape");
}

/** A local-state section switch: a tab on the title line, a radio in the toolbar row below `lg`. */
export function sectionSwitch(page: Page, name: string, exact = false): Locator {
  return page.getByRole("tab", { name, exact }).or(page.getByRole("radio", { name, exact }));
}

/** Whether a section switch is the chosen one, whichever way it is drawn. */
export async function expectSectionChosen(page: Page, name: string): Promise<void> {
  const choice = sectionSwitch(page, name);
  await expect(choice).toBeVisible();
  const attribute = (await choice.getAttribute("aria-selected")) !== null ? "aria-selected" : "aria-checked";
  await expect(choice).toHaveAttribute(attribute, "true");
}

/** Chooses a local-state section wherever it is drawn: on the title line, in the row below `lg`, or under "More". */
export async function openSection(page: Page, name: string, exact = false): Promise<void> {
  const choice = sectionSwitch(page, name, exact);
  const more = page.getByRole("button", { name: "More", exact: true });
  // Wait for the page to draw its sections before deciding where this one is.
  await choice.or(more).first().waitFor();
  if (await choice.count()) {
    try {
      await choice.first().click({ timeout: 5_000 });
      return;
    } catch {
      // Folded into More while it was being clicked.
    }
  }
  await more.click();
  await page.getByRole("menuitem", { name, exact }).click();
}
