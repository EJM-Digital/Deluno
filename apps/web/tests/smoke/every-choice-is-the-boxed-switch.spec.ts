import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { trackDataRequests } from "../helpers/data-settled";
import { settingsRoutes, sidebarRoutes } from "../helpers/route-lists";

// #1530. Every filter and choice is the boxed switch (the owner, 7 Nov 2026), the same as Weir's .mm-segmented:
// a 1px hairline box, radius 12, padding 3; options 30px tall, radius 10, 13px, the chosen one tinted.
// Radiogroups that list choices with a sentence under each (data-choice-rows) are rows, not switches, and are left out.
// The walk visits every route and fails on any switch drawn differently.

test("every radiogroup on every screen is the boxed switch", async ({ page }) => {
  test.setTimeout(240_000);
  await authenticateAndNavigate(page, "/movies");
  const data = trackDataRequests(page);
  const routes = [...new Set(["/", "/queue?view=handoffs", "/queue?view=history", "/system", ...sidebarRoutes(), ...settingsRoutes()])];
  const offenders: string[] = [];
  let seen = 0;
  for (const route of routes) {
    await page.goto(route);
    // The guided setup saves its draft as you go; its request stays open, so it is given a moment instead of waiting for quiet.
    if (route === "/setup-guide") await page.waitForTimeout(2500); // reason: the draft save never goes quiet
    else await data.settled();
    const found = await page.evaluate(() => {
      const px = (value: string) => Math.round(parseFloat(value) * 10) / 10;
      const out: string[] = [];
      let count = 0;
      for (const group of Array.from(document.querySelectorAll<HTMLElement>('[role="radiogroup"]:not([data-choice-rows])'))) {
        const box = group.getBoundingClientRect();
        if (box.width === 0 || box.height === 0) continue;
        count += 1;
        const style = getComputedStyle(group);
        const name = group.getAttribute("aria-label") ?? "(unnamed)";
        const wrong: string[] = [];
        if (px(style.borderTopLeftRadius) !== 12) wrong.push(`radius ${style.borderTopLeftRadius}`);
        if (px(style.borderTopWidth) !== 1) wrong.push(`border ${style.borderTopWidth}`);
        if (px(style.paddingTop) !== 3 || px(style.paddingLeft) !== 3) wrong.push(`padding ${style.paddingTop}/${style.paddingLeft}`);
        for (const option of Array.from(group.querySelectorAll<HTMLElement>('[role="radio"]'))) {
          const o = getComputedStyle(option);
          const r = option.getBoundingClientRect();
          if (r.width === 0) continue;
          const bad: string[] = [];
          if (Math.round(r.height) !== 30) bad.push(`height ${r.height}`);
          if (px(o.borderTopLeftRadius) !== 10) bad.push(`radius ${o.borderTopLeftRadius}`);
          if (px(o.fontSize) !== 13) bad.push(`font ${o.fontSize}`);
          if (option.getAttribute("aria-checked") === "true") {
            if (o.backgroundColor === "rgba(0, 0, 0, 0)" || o.backgroundColor === "transparent") bad.push("selected has no fill");
            if (px(o.borderTopWidth) !== 1 || o.borderTopColor === "rgba(0, 0, 0, 0)") bad.push("selected has no border");
          }
          if (bad.length > 0) wrong.push(`option "${(option.textContent ?? "").trim().slice(0, 20)}": ${bad.join(", ")}`);
        }
        if (wrong.length > 0) out.push(`"${name}": ${wrong.slice(0, 3).join("; ")}`);
      }
      return { out, count };
    });
    seen += found.count;
    for (const line of found.out) offenders.push(`${route} ${line}`);
  }
  expect(seen, "the walk found switches to measure").toBeGreaterThan(5);
  expect(offenders, offenders.join("\n")).toEqual([]);
});
