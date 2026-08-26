import { test, expect } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * #290 — navigation colour must not collide with the status palette.
 *
 * Three of the six old nav accents were byte-for-byte the values of
 * `--success`, `--warning` and `--info`, and they were lit at rest. Green meant
 * *Healthy* on the dashboard and *Find & Download* in the sidebar at the same
 * time. Healthy is a colour you scan for the absence of, so a permanently
 * part-green sidebar made that scan meaningless.
 *
 * This asserts the rule rather than a particular shade: nothing in the shell
 * resolves to a semantic hue, in either theme.
 */

/** The reserved tokens, read from the page so the test cannot drift from the CSS. */
async function semanticColours(page: import("@playwright/test").Page) {
  return page.evaluate(() => {
    const root = document.documentElement;
    const styles = getComputedStyle(root);
    const probe = document.createElement("span");
    document.body.append(probe);
    const resolved = ["--success", "--warning", "--destructive", "--state-ok", "--state-warn", "--state-danger"]
      .map((token) => styles.getPropertyValue(token).trim())
      .filter(Boolean)
      .map((value) => {
        probe.style.color = `hsl(${value})`;
        return getComputedStyle(probe).color;
      });
    probe.remove();
    return [...new Set(resolved)];
  });
}

/** Every colour the shell actually paints, resting and active alike. */
async function shellColours(page: import("@playwright/test").Page) {
  return page.evaluate(() => {
    const roots = [...document.querySelectorAll("nav, [data-shell-nav]")];
    const seen = new Set<string>();
    for (const root of roots) {
      for (const element of [root, ...root.querySelectorAll("*")]) {
        const styles = getComputedStyle(element);
        seen.add(styles.color);
        seen.add(styles.backgroundColor);
        seen.add(styles.borderTopColor);
      }
    }
    return [...seen].filter((value) => value && value !== "rgba(0, 0, 0, 0)");
  });
}

for (const theme of ["light", "dark"] as const) {
  test(`no navigation element uses a semantic hue in ${theme} mode`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: theme });
    await authenticateAndNavigate(page, "/movies");
    await page.waitForTimeout(500);

    const reserved = await semanticColours(page);
    expect(reserved.length).toBeGreaterThan(0);

    const painted = await shellColours(page);
    expect(painted.length).toBeGreaterThan(0);

    for (const colour of painted) {
      expect(reserved, `${colour} is a reserved status colour and must not appear in navigation`).not.toContain(colour);
    }
  });
}

test("the active area is identifiable without colour", async ({ page }) => {
  await authenticateAndNavigate(page, "/movies");

  // Weight and the rail carry it, so a greyscale screenshot and a colour-blind
  // reader both still know where they are. Scoped to what is on screen: the
  // desktop sidebar and the mobile tab bar are both in the DOM at all times, and
  // only one of them is the navigation the reader can actually see.
  const active = page.locator("nav a[aria-current='page']:visible").first();
  await expect(active).toBeVisible();
  const weight = await active.evaluate((element) => getComputedStyle(element).fontWeight);
  expect(Number(weight)).toBeGreaterThanOrEqual(600);
});
