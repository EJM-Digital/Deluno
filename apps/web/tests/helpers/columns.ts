import { expect, type Locator, type Page } from "@playwright/test";

/**
 * The shared list-column grammar (#1119) on a real screen: click a header to sort (ascending, then descending), drag a
 * header to reorder, both remembered in this browser across a reload, and Reset columns (behind the card's Columns
 * button) puts them back. Used by the real specs of each list screen so a table is proven with its own rows.
 */
export interface ColumnsPlan {
  /** Finds the table's scope again after a reload. */
  scope: () => Locator;
  /** The column ids in the order the screen starts with. */
  defaultOrder: string[];
  /** The sortable column and the texts of its cells, in table order, for rows this test made (filtered by its stamp). */
  sortId: string;
  cellsOf: (scope: Locator) => Promise<string[]>;
  /** The Columns button of this table, when the page has more than one. */
  columnsButton?: () => Locator;
  /** A column dragged in front of another. */
  move: { from: string; onto: string };
}

export const headerOrder = (scope: Locator) => scope.getByRole("columnheader").evaluateAll((headers) => headers.map((h) => (h as HTMLElement).dataset.colId));

async function dragHeader(page: Page, scope: Locator, from: string, onto: string) {
  const source = await scope.locator(`[role="columnheader"][data-col-id="${from}"]`).boundingBox();
  const target = await scope.locator(`[role="columnheader"][data-col-id="${onto}"]`).boundingBox();
  if (!source || !target) throw new Error("column headers not found");
  const y = source.y + source.height / 2;
  await page.mouse.move(source.x + 12, y);
  await page.mouse.down();
  await page.mouse.move(source.x + 24, y, { steps: 3 });
  await page.mouse.move(target.x + 10, y, { steps: 12 });
  await page.mouse.up();
}

export async function exerciseColumns(page: Page, plan: ColumnsPlan) {
  const header = (id: string) => plan.scope().locator(`[role="columnheader"][data-col-id="${id}"]`);
  await page.evaluate(() => Object.keys(localStorage).filter((k) => /column|sort|list/i.test(k) && !/auth|token/i.test(k)).forEach((k) => localStorage.removeItem(k)));
  await page.reload();
  await expect(header(plan.sortId)).toBeVisible();
  expect((await headerOrder(plan.scope())).slice(0, plan.defaultOrder.length)).toEqual(plan.defaultOrder);

  const sortButton = header(plan.sortId).getByRole("button");
  await sortButton.click();
  await expect(header(plan.sortId)).toHaveAttribute("aria-sort", "ascending");
  const ascending = await plan.cellsOf(plan.scope());
  expect(ascending.length).toBeGreaterThan(1);
  expect(ascending).toEqual([...ascending].sort((a, b) => a.localeCompare(b)));
  await sortButton.click();
  await expect(header(plan.sortId)).toHaveAttribute("aria-sort", "descending");
  expect(await plan.cellsOf(plan.scope())).toEqual([...ascending].reverse());

  await dragHeader(page, plan.scope(), plan.move.from, plan.move.onto);
  await expect.poll(async () => { const order = await headerOrder(plan.scope()); return order.indexOf(plan.move.from) < order.indexOf(plan.move.onto); }).toBe(true);

  const moved = await headerOrder(plan.scope());
  await page.reload();
  await expect(header(plan.sortId)).toHaveAttribute("aria-sort", "descending");
  expect(await headerOrder(plan.scope())).toEqual(moved);

  await (plan.columnsButton?.() ?? plan.scope().getByRole("button", { name: "Columns", exact: true })).click();
  await page.getByRole("menuitem", { name: "Reset columns" }).click();
  await expect(header(plan.sortId)).not.toHaveAttribute("aria-sort");
  expect((await headerOrder(plan.scope())).slice(0, plan.defaultOrder.length)).toEqual(plan.defaultOrder);
}
