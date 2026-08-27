import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

/**
 * Anchored to this file, not to `process.cwd()`.
 *
 * With cwd, the screenshots landed in `apps/web/test-results/` when the suite
 * was run through the workspace script and in `<repo>/test-results/` when it
 * was run from the root — and only the first of those is in `.gitignore`, so
 * running it the other way left untracked artefacts sitting in `git status`,
 * ready to be swept into somebody's commit.
 */
const reviewDir = process.env.DELUNO_MOBILE_REVIEW_DIR
  ?? path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "test-results", "mobile-review");

/**
 * A look at the pages on a phone, rather than an assertion that their elements
 * exist (#278). The suite already runs a mobile project, so mobile has been
 * *tested* for a long time without anyone having *seen* it.
 *
 * Deliberately captures rather than asserts: the output is screenshots to look
 * at and two measurements that catch the faults you cannot see in a single
 * viewport — a page wider than the phone, and a tap target too small to hit.
 */
test.describe("mobile review", () => {
  const pages = [
    { name: "dashboard", path: "/" },
    { name: "movies", path: "/movies" },
    { name: "queue", path: "/queue" },
    { name: "find-and-download", path: "/indexers/indexers" },
    { name: "media-management", path: "/settings/processing" }
  ];

  for (const target of pages) {
    test(`captures ${target.name}`, async ({ page }, testInfo) => {
      test.skip(testInfo.project.name !== "mobile", "This is the phone review.");

      await authenticateAndNavigate(page, target.path);
      await page.waitForTimeout(1500);

      fs.mkdirSync(reviewDir, { recursive: true });
      await page.screenshot({ path: path.join(reviewDir, `${target.name}.png`), fullPage: true });

      // A phone page that scrolls sideways is broken, always. Every wide thing
      // on this app — tables, charts, stage strips — is supposed to scroll
      // inside its own box rather than take the page with it.
      const overflow = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        clientWidth: document.documentElement.clientWidth
      }));
      expect(
        overflow.scrollWidth,
        `${target.name} scrolls sideways on a phone`
      ).toBeLessThanOrEqual(overflow.clientWidth + 1);

      // Anything you are meant to tap needs to be big enough to tap. 24px is
      // well under the 44px guideline and is only meant to catch the genuinely
      // unhittable.
      const tooSmall = await page.evaluate(() => {
        const targets = [...document.querySelectorAll("a[href], button")];
        return targets
          .filter((element) => {
            const box = element.getBoundingClientRect();
            if (box.width === 0 || box.height === 0) return false;
            const style = getComputedStyle(element);
            if (style.visibility === "hidden" || style.display === "none") return false;
            return box.height < 24 || box.width < 24;
          })
          .slice(0, 8)
          .map((element) => ({
            text: (element.textContent ?? "").trim().slice(0, 40),
            label: element.getAttribute("aria-label"),
            height: Math.round(element.getBoundingClientRect().height),
            width: Math.round(element.getBoundingClientRect().width)
          }));
      });

      // The bottom tab bar is fixed, so the page has to end above it. Without
      // that clearance the last card on every page is permanently half-hidden,
      // which is invisible in a screenshot of the top of the page.
      const bottomClearance = await page.evaluate(async () => {
        window.scrollTo(0, document.documentElement.scrollHeight);
        await new Promise((resolve) => setTimeout(resolve, 400));
        const bars = [...document.querySelectorAll("nav, [role=navigation]")]
          .filter((element) => getComputedStyle(element).position === "fixed")
          .map((element) => element.getBoundingClientRect())
          .filter((box) => box.height > 0 && box.bottom >= window.innerHeight - 2);
        if (bars.length === 0) return { hasFixedBar: false, overlapPx: 0 };

        const barTop = Math.min(...bars.map((box) => box.top));
        const main = document.querySelector("main") ?? document.body;
        const lastChild = [...main.querySelectorAll(":scope > *")].at(-1);
        const lastBottom = lastChild ? lastChild.getBoundingClientRect().bottom : 0;
        return { hasFixedBar: true, barTop: Math.round(barTop), lastBottom: Math.round(lastBottom), overlapPx: Math.round(lastBottom - barTop) };
      });

      // Stacked on a phone, the list of things asking for a decision has to come
      // before the diagnostics panel. Side by side they read left-to-right and
      // the order is the other way round, so this only holds below the
      // breakpoint (#278).
      let stackedOrder: { needsYouTop: number; systemPulseTop: number } | null = null;
      if (target.path === "/") {
        stackedOrder = await page.evaluate(() => {
          const sections = [...document.querySelectorAll("section")];
          const pulse = sections.find((s) => s.getAttribute("aria-label") === "System status");
          // innerText reflects CSS text-transform in Chrome, so the card's own
          // title casing is not what comes back here.
          const needs = sections.find((s) => s.innerText.toLowerCase().startsWith("needs you"));
          return pulse && needs
            ? {
                needsYouTop: Math.round(needs.getBoundingClientRect().top + window.scrollY),
                systemPulseTop: Math.round(pulse.getBoundingClientRect().top + window.scrollY)
              }
            : null;
        });

        if (stackedOrder) {
          expect(
            stackedOrder.needsYouTop,
            "on a phone, Needs you must come before System pulse"
          ).toBeLessThan(stackedOrder.systemPulseTop);
        }
      }

      fs.writeFileSync(
        path.join(reviewDir, `${target.name}.json`),
        JSON.stringify({ viewport: page.viewportSize(), overflow, bottomClearance, stackedOrder, tooSmall }, null, 2)
      );
    });
  }
});
