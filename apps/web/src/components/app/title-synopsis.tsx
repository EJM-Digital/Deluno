import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { cn } from "../../lib/utils";

const wide = "(min-width: 1280px)";

/**
 * The synopsis in a title page's header (#1058). At xl widths the header is as
 * tall as the poster and the middle column fills it, so the synopsis takes the
 * height that is left and is cut to the whole lines that fit. "More" opens the
 * rest in place (the header then grows; that is the only time it does) and
 * "Less" puts it back. Narrower than xl the column stacks under the poster, has
 * no height rule and shows the whole text.
 */
export function TitleSynopsis({ text }: { text: string }) {
  const box = useRef<HTMLDivElement>(null);
  const paragraph = useRef<HTMLParagraphElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [lines, setLines] = useState<number | null>(null);

  const measure = useCallback(() => {
    const boxEl = box.current;
    const p = paragraph.current;
    if (!boxEl || !p) return;
    if (!window.matchMedia(wide).matches || expanded) {
      setLines(null);
      return;
    }
    const previous = p.style.webkitLineClamp;
    const previousDisplay = p.style.display;
    p.style.webkitLineClamp = "unset";
    p.style.display = "block";
    const lineHeight = parseFloat(getComputedStyle(p).lineHeight) || 20;
    const natural = p.scrollHeight;
    p.style.display = previousDisplay;
    p.style.webkitLineClamp = previous;
    const available = boxEl.clientHeight;
    if (natural <= available + 1) {
      setLines(null);
      return;
    }
    // Leave one line's height for the More button.
    setLines(Math.max(1, Math.floor((available - 21) / lineHeight)));
  }, [expanded]);

  useLayoutEffect(() => {
    measure();
  }, [measure, text]);

  useEffect(() => {
    const boxEl = box.current;
    if (!boxEl || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => measure());
    observer.observe(boxEl);
    const query = window.matchMedia(wide);
    query.addEventListener?.("change", measure);
    return () => {
      observer.disconnect();
      query.removeEventListener?.("change", measure);
    };
  }, [measure]);

  const clamped = lines !== null;
  return (
    <div
      ref={box}
      data-expanded={expanded ? "true" : "false"}
      data-testid="title-synopsis"
      className={cn("mt-2 max-w-4xl min-w-0", !expanded && "xl:min-h-0 xl:flex-1 xl:overflow-hidden")}
    >
      <p
        ref={paragraph}
        className="text-sm leading-relaxed text-muted-foreground"
        style={clamped ? { display: "-webkit-box", WebkitBoxOrient: "vertical", WebkitLineClamp: lines ?? undefined, overflow: "hidden" } : undefined}
      >
        {text}
      </p>
      {clamped || expanded ? (
        <button
          type="button"
          onClick={() => setExpanded((value) => !value)}
          aria-expanded={expanded}
          className="block text-xs font-semibold leading-5 text-primary hover:underline"
        >
          {expanded ? "Less" : "More"}
        </button>
      ) : null}
    </div>
  );
}
