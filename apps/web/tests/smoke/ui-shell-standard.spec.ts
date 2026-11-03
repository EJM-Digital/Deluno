import { expect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { waitForLayoutCalm } from "../helpers/layout-calm";

// #1461. The shared shell standard (docs/ui-shell-standard.md): Deluno and Weir draw the page eyebrow, page title,
// top-card label and panel title identically, so the top of the page does not shift when switching apps. Each element's
// computed font size, weight, letter spacing, line height and top are read and compared with the spec.

type Spec = { name: string; selector: string; size: number; weight: string; spacingEm: number; lineHeight: number; top?: number; text?: boolean };

const SPECS: Spec[] = [
  { name: "page eyebrow", selector: "header#topbar-row p, #topbar-main p", size: 11, weight: "700", spacingEm: 0.14, lineHeight: 14, top: 14 },
  { name: "page title", selector: "#topbar-title > *", size: 26, weight: "600", spacingEm: -0.03, lineHeight: NaN, top: 32 },
  { name: "top-card label", selector: ".lb-label", size: 11, weight: "600", spacingEm: 0.12, lineHeight: 16 },
  { name: "panel title", selector: "main section > header > h2", size: 15, weight: "600", spacingEm: NaN, lineHeight: 23 }
];

async function read(page: Page, selector: string) {
  return page.locator(selector).first().evaluate((el) => {
    const style = getComputedStyle(el);
    const size = parseFloat(style.fontSize);
    return {
      size,
      weight: style.fontWeight,
      spacing: style.letterSpacing === "normal" ? 0 : parseFloat(style.letterSpacing),
      lineHeight: style.lineHeight === "normal" ? NaN : parseFloat(style.lineHeight),
      top: el.getBoundingClientRect().top,
      upper: style.textTransform
    };
  });
}

async function check(page: Page, specs: Spec[]) {
  const failures: string[] = [];
  for (const spec of specs) {
    await expect(page.locator(spec.selector).first(), `${spec.name} is on the page`).toBeVisible();
    const got = await read(page, spec.selector);
    if (Math.abs(got.size - spec.size) > 0.01) failures.push(`${spec.name}: font size ${got.size}, spec ${spec.size}`);
    if (got.weight !== spec.weight) failures.push(`${spec.name}: weight ${got.weight}, spec ${spec.weight}`);
    if (!Number.isNaN(spec.spacingEm) && Math.abs(got.spacing - spec.spacingEm * spec.size) > 0.02) failures.push(`${spec.name}: letter spacing ${got.spacing}px, spec ${(spec.spacingEm * spec.size).toFixed(3)}px`);
    if (!Number.isNaN(spec.lineHeight) && Math.abs(got.lineHeight - spec.lineHeight) > 0.01) failures.push(`${spec.name}: line height ${got.lineHeight}, spec ${spec.lineHeight}`);
    if (spec.top !== undefined && Math.abs(got.top - spec.top) > 0.5) failures.push(`${spec.name}: top ${got.top}, spec ${spec.top}`);
  }
  expect(failures, failures.join("\n")).toEqual([]);
}

for (const size of [{ width: 2560, height: 1300 }, { width: 1366, height: 768 }]) {
  test.describe(`${size.width}x${size.height}`, () => {
    test.use({ viewport: size });

    test("the Dashboard draws the eyebrow, title, top-card labels and panel titles to the shell standard", async ({ page }) => {
      await authenticateAndNavigate(page, "/");
      await waitForLayoutCalm(page);
      await check(page, SPECS);
    });

    test("a Settings page draws the eyebrow and title to the shell standard", async ({ page }) => {
      await authenticateAndNavigate(page, "/settings/general");
      await waitForLayoutCalm(page);
      await check(page, SPECS.slice(0, 2));
    });
  });
}
