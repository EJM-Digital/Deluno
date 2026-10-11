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
  const more = page.getByRole("button", { name: "More", exact: true });
  await expect(link.or(more)).not.toHaveCount(0);
  // The row re-measures while the page settles and folds what does not fit into More, so one look can land between
  // the two: drawn after More was opened, or folded after the row was read. Look again until the tab is in one of the
  // places, and if it never is, say what each place held so a real gap is told apart from a slow layout.
  let seen = "";
  await expect.poll(async () => {
    if (await link.count()) return true;
    if (!(await more.count())) return false;
    await more.click();
    const items = page.getByRole("menuitem");
    await items.first().waitFor({ timeout: 2_000 }).catch(() => undefined);
    const listed = await items.allInnerTexts();
    await page.keyboard.press("Escape");
    const drawn = await page.getByRole("navigation", { name: "Sections" }).getByRole("link").allInnerTexts();
    seen = `row: [${drawn.join(", ")}]; More: [${listed.join(", ")}]`;
    return listed.some((text) => text.trim() === name);
  }, { message: () => `"${name}" is neither a tab nor under More (${seen})`, timeout: 15_000 }).toBe(true);
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
