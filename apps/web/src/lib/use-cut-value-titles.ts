import { useEffect } from "react";

/**
 * A value the screen cuts short with an ellipsis or a line clamp tells its full text on hover or focus, even where no
 * one gave it a `title` (#1168). Components that know their text set a title themselves; this is the net under every
 * other one, so a long caption or version number is never cut with no way to read it. Nothing is written until the
 * pointer or focus reaches a value that is really cut, and a title already there is left alone.
 */
export function useCutValueTitles() {
  useEffect(() => {
    const isCut = (element: HTMLElement) => {
      const style = getComputedStyle(element);
      const sideways = style.textOverflow === "ellipsis" && style.overflowX !== "visible" && element.scrollWidth > element.clientWidth + 1;
      const clamped = style.getPropertyValue("-webkit-line-clamp") !== "none" && style.overflowY !== "visible" && element.scrollHeight > element.clientHeight + 1;
      return sideways || clamped;
    };
    const handle = (event: Event) => {
      let element = event.target instanceof HTMLElement ? event.target : null;
      for (let depth = 0; element && depth < 4; depth += 1, element = element.parentElement) {
        if (element.hasAttribute("title")) return;
        if (isCut(element)) {
          const text = element.textContent.replace(/\s+/g, " ").trim();
          if (text) element.setAttribute("title", text);
          return;
        }
      }
    };
    document.addEventListener("pointerover", handle, true);
    document.addEventListener("focusin", handle, true);
    return () => {
      document.removeEventListener("pointerover", handle, true);
      document.removeEventListener("focusin", handle, true);
    };
  }, []);
}
