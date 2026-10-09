import { expect, type Page } from "@playwright/test";

/**
 * Waits until the page has stopped moving, so a measurement is of the finished layout and not of one frame of it (#1191).
 *
 * Three things move a layout after the first paint, and a busy machine stretches each: web fonts arriving (text re-wraps),
 * images arriving (a poster gives its box a height), and a CSS transition or finite animation still running (a card
 * opening reads as 0px, 8px, ... until it ends). A fixed sleep guesses how long they take; this waits for each to be done.
 * Endless animations (a spinner, a pulse) never finish, so they are not waited for.
 */
export async function waitForLayoutCalm(page: Page, timeout = 15_000) {
  await page.evaluate(() => document.fonts.ready);
  await expect.poll(() => page.evaluate(() => {
    const unfinished = document.getAnimations().filter((animation) => {
      if (animation.playState !== "running" && animation.playState !== "pending") return false;
      return animation.effect?.getComputedTiming().iterations !== Infinity;
    }).length;
    const loading = Array.from(document.images).filter((image) => !image.complete && image.loading !== "lazy").length;
    return unfinished + loading;
  }), { timeout, message: "the page kept animating or loading images" }).toBe(0);
}
