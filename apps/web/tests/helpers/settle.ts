import type { Locator, Page } from "@playwright/test";

/**
 * Waits for what a fixed pause used to guess at: the page has stopped changing.
 *
 * It watches the page once per animation frame and returns when its height, width, element count and
 * "busy" markers have all stayed the same for `frames` frames in a row and nothing is marked busy. A late list,
 * a scrolled-in row or a finished fetch changes one of them, so the wait ends when the change is done and not
 * after a guessed time. The limit is `timeout`: a page that never goes quiet fails with what it was still doing.
 * Defaults: 30 frames is about half a second of calm, enough for a render that follows a fetch that follows a
 * click; 15 seconds is longer than any screen takes on the slowest machine we test on.
 */
export async function untilQuiet(page: Page, options: { frames?: number; timeout?: number } = {}): Promise<void> {
  const frames = options.frames ?? 30;
  const timeout = options.timeout ?? 15_000;
  await page.evaluate(
    ({ frames: needed, timeout: limit }) =>
      new Promise<void>((resolve, reject) => {
        const started = performance.now();
        const read = () => {
          const root = document.documentElement;
          const busy = document.querySelectorAll('[aria-busy="true"]').length;
          return { busy, signature: [root.scrollHeight, root.scrollWidth, document.getElementsByTagName("*").length, busy].join("/") };
        };
        let last = read().signature;
        let calm = 0;
        const tick = () => {
          const now = read();
          calm = now.signature === last ? calm + 1 : 0;
          last = now.signature;
          if (calm >= needed && now.busy === 0) {
            void document.fonts.ready.then(() => resolve());
          } else if (performance.now() - started > limit) {
            reject(new Error(`The page was still changing after ${limit} ms (height/width/elements/busy: ${now.signature}).`));
          } else {
            requestAnimationFrame(tick);
          }
        };
        requestAnimationFrame(tick);
      }),
    { frames, timeout }
  );
}

/**
 * Waits until an element has stopped moving (a drawer or menu that slides in): its box is the same for
 * `frames` frames in a row. The limit is `timeout`.
 */
export async function untilStill(target: Locator, options: { frames?: number; timeout?: number } = {}): Promise<void> {
  const frames = options.frames ?? 10;
  const timeout = options.timeout ?? 5_000;
  await target.evaluate(
    (element, { frames: needed, timeout: limit }) =>
      new Promise<void>((resolve, reject) => {
        const started = performance.now();
        const read = () => {
          const box = element.getBoundingClientRect();
          return [box.x, box.y, box.width, box.height].join("/");
        };
        let last = read();
        let calm = 0;
        const tick = () => {
          const now = read();
          calm = now === last ? calm + 1 : 0;
          last = now;
          if (calm >= needed) resolve();
          else if (performance.now() - started > limit) reject(new Error(`The element was still moving after ${limit} ms (${now}).`));
          else requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
      }),
    { frames, timeout }
  );
}
