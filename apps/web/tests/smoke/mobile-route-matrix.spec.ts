import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";
import { trackDataRequests } from "../helpers/data-settled";

/**
 * #341: every owner workflow reachable on desktop is reachable on mobile, with
 * no unintentional horizontal overflow and no nested-scroll traps.
 *
 * <p>The mobile project already runs every other spec at Pixel 7 size, which
 * proves the flows work. What it does not check is the thing that actually
 * breaks a phone: a page whose body scrolls sideways because one table or one
 * pre block is wider than the screen. That is invisible to an assertion about
 * a button, and obvious the moment you hold the device.</p>
 *
 * <p>Wide content is allowed to scroll — inside its own container. The body
 * never is.</p>
 */

const OWNER_ROUTES = [
  "/",
  "/movies",
  "/tv",
  "/collections",
  "/calendar",
  "/queue",
  "/activity",
  "/settings/libraries",
  "/settings/policy-sets",
  "/settings/profiles",
  "/settings/quality",
  "/settings/custom-formats",
  "/settings/release-rules",
  "/settings/media-management",
  "/settings/destination-rules",
  "/settings/lists",
  "/settings/automation",
  "/settings/metadata",
  "/settings/general",
  "/settings/tags",
  "/indexers/indexers",
  "/indexers/download-clients",
  "/indexers/subtitle-providers",
  "/search-cycles",
  "/system",
  "/system/backups",
  "/system/updates"
] as const;

test.describe("mobile route matrix", () => {
  // Only meaningful at phone width; the desktop project would pass trivially.
  test.skip(({ isMobile }) => !isMobile, "Phone-width behaviour only.");

  test("no owner route scrolls the page sideways", async ({ page }) => {
    // 27 full navigations, each waiting out trackDataRequests' quiet window,
    // is several times the work of a typical smoke test (crud-flows.spec.ts
    // sets its own 60s budget for the same reason), so this gets the same
    // treatment rather than the default 30s.
    //
    // A first attempt at explaining a CI-only failure here raised this to
    // 90s and blamed general load, but the real cause was `useAttention`
    // (apps/web/src/lib/use-attention.ts, mounted for every route via
    // app-layout.tsx): its `load()` had no guard against a second call
    // landing while its own eleven-fetch batch was still in flight. On a
    // quiet install that never matters, but every ActivityEventAdded/
    // HealthChanged envelope also called it, and a CI runner slow enough
    // (CPU-throttled, sharing one dev server with hundreds of prior tests'
    // background jobs still draining) let a burst of those land mid-batch --
    // each one starting a fresh batch on top of the last, so the number of
    // outstanding requests only grew and trackDataRequests().settled() never
    // saw its 500ms of quiet. Fixed at the source with an in-flight guard;
    // this budget stays for the genuine cost of 27 real navigations under
    // suite load, not for that hang.
    test.setTimeout(60_000);
    await authenticateAndNavigate(page, "/movies");
    const data = trackDataRequests(page);

    const offenders: string[] = [];
    for (const route of OWNER_ROUTES) {
      await page.goto(route);
      await data.settled();

      const overflow = await page.evaluate(() => {
        const doc = document.documentElement;
        const overshoot = doc.scrollWidth - doc.clientWidth;
        if (overshoot <= 1) return null;

        // Name the widest element that is actually sticking out, because
        // "the page is too wide" is not something anybody can act on.
        let worst = { tag: "", width: 0, text: "" };
        for (const el of Array.from(document.querySelectorAll<HTMLElement>("body *"))) {
          const rect = el.getBoundingClientRect();
          if (rect.right > doc.clientWidth + 1 && rect.width > worst.width) {
            worst = {
              tag: `${el.tagName.toLowerCase()}${el.className ? "." + String(el.className).split(/\s+/).slice(0, 2).join(".") : ""}`,
              width: Math.round(rect.width),
              text: (el.textContent ?? "").trim().slice(0, 40)
            };
          }
        }
        return { overshoot, worst };
      });

      if (overflow) {
        offenders.push(
          `${route}: ${overflow.overshoot}px past the viewport — widest offender ${overflow.worst.tag} `
          + `(${overflow.worst.width}px) “${overflow.worst.text}”`
        );
      }
    }

    expect(offenders, offenders.join("\n")).toEqual([]);
  });

  test("wide content scrolls inside its own container, not the page", async ({ page }) => {
    // The settings screens are where the wide tables live, so they are where
    // the rule has to hold.
    await authenticateAndNavigate(page, "/settings/quality");
    const data = trackDataRequests(page);

    for (const route of ["/settings/quality", "/settings/custom-formats", "/settings/policy-sets", "/queue"]) {
      await page.goto(route);
      await data.settled();

      const bodyScrolls = await page.evaluate(() =>
        document.documentElement.scrollWidth - document.documentElement.clientWidth > 1);
      expect(bodyScrolls, `${route} scrolls the page body sideways`).toBe(false);
    }
  });
});
