/**
 * Whether a value change may be shown as movement right now (#873).
 *
 * Browsers pause requestAnimationFrame while a tab is hidden or its window is
 * covered. A tween started then does not run; it waits, and plays the moment
 * the page is looked at again, so a number that changed an hour ago counts up
 * from its old value as if it had just happened. The data was already current
 * (#641); only the drawing lagged.
 *
 * So movement is only for changes the person can see happen: when the page is
 * not visible, or motion is reduced, the value is simply shown.
 */
export function motionAllowed(): boolean {
  if (typeof document !== "undefined" && document.visibilityState !== "visible") return false;
  if (typeof window !== "undefined" && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) return false;
  return true;
}

/**
 * Calls `settle` when the page stops being visible, so an animation already
 * under way lands on its final value instead of resuming from the middle when
 * the page is shown again. Returns the unsubscribe.
 */
export function onPageHidden(settle: () => void): () => void {
  if (typeof document === "undefined") return () => {};
  const listener = () => {
    if (document.visibilityState !== "visible") settle();
  };
  document.addEventListener("visibilitychange", listener);
  return () => document.removeEventListener("visibilitychange", listener);
}

/**
 * Marks the document while it is hidden, and for two frames after it is shown
 * again, so CSS can switch transitions off (#873). A progress bar or donut whose
 * value changed in the background would otherwise run its transition from the
 * old value when the page is looked at. Two frames lets the browser apply the
 * current values with transitions still off before movement is allowed again.
 */
export function installHiddenPageMotionGuard(doc: Document = document, win: Window = window): () => void {
  const root = doc.documentElement;
  const mark = () => root.setAttribute("data-page-hidden", "");
  const sync = () => {
    if (doc.visibilityState !== "visible") {
      mark();
      return;
    }
    win.requestAnimationFrame(() => win.requestAnimationFrame(() => {
      if (doc.visibilityState === "visible") root.removeAttribute("data-page-hidden");
    }));
  };
  if (doc.visibilityState !== "visible") mark();
  doc.addEventListener("visibilitychange", sync);
  return () => doc.removeEventListener("visibilitychange", sync);
}
