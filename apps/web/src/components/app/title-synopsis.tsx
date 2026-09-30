import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { X } from "lucide-react";
import { cn } from "../../lib/utils";

const wide = "(min-width: 1280px)";

/**
 * The synopsis in a title page's header (#1058). At xl widths the header is as
 * tall as the poster and the middle column fills it, so the synopsis takes the
 * height that is left and is cut to the whole lines that fit. "More" opens the
 * full text in a floating panel over the page, so the header never changes
 * height. Narrower than xl the column stacks under the poster, has no height
 * rule and shows the whole text.
 */
export function TitleSynopsis({ text }: { text: string }) {
  const box = useRef<HTMLDivElement>(null);
  const paragraph = useRef<HTMLParagraphElement>(null);
  const moreButton = useRef<HTMLButtonElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [anchor, setAnchor] = useState<{ top: number; left: number; width: number } | null>(null);
  const [lines, setLines] = useState<number | null>(null);

  const measure = useCallback(() => {
    const boxEl = box.current;
    const p = paragraph.current;
    if (!boxEl || !p) return;
    if (!window.matchMedia(wide).matches) {
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
  }, []);

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

  const close = useCallback(() => {
    setOpen(false);
    moreButton.current?.focus();
  }, []);

  useEffect(() => {
    if (!open) return;
    closeButton.current?.focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.stopPropagation();
        close();
      }
    };
    const onDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (box.current?.contains(target) || document.getElementById("title-synopsis-popover")?.contains(target)) return;
      setOpen(false);
    };
    const onResize = () => setOpen(false);
    document.addEventListener("keydown", onKey, true);
    document.addEventListener("mousedown", onDown);
    window.addEventListener("resize", onResize);
    return () => {
      document.removeEventListener("keydown", onKey, true);
      document.removeEventListener("mousedown", onDown);
      window.removeEventListener("resize", onResize);
    };
  }, [open, close]);

  const openPanel = () => {
    const rect = box.current?.getBoundingClientRect();
    if (!rect) return;
    setAnchor({ top: rect.top, left: rect.left, width: Math.max(rect.width, 320) });
    setOpen(true);
  };

  const clamped = lines !== null;
  return (
    <div
      ref={box}
      data-testid="title-synopsis"
      className="mt-2 max-w-4xl min-w-0 xl:min-h-0 xl:flex-1 xl:overflow-hidden"
    >
      <p
        ref={paragraph}
        className="text-sm leading-relaxed text-muted-foreground"
        style={clamped ? { display: "-webkit-box", WebkitBoxOrient: "vertical", WebkitLineClamp: lines ?? undefined, overflow: "hidden" } : undefined}
      >
        {text}
      </p>
      {clamped ? (
        <button
          ref={moreButton}
          type="button"
          onClick={openPanel}
          aria-haspopup="dialog"
          aria-expanded={open}
          aria-label="Read the full synopsis"
          className="block text-xs font-semibold leading-5 text-primary hover:underline"
        >
          More
        </button>
      ) : null}
      {open && anchor
        ? createPortal(
            <div
              id="title-synopsis-popover"
              role="dialog"
              aria-label="Synopsis"
              data-testid="title-synopsis-popover"
              style={{ position: "fixed", top: anchor.top, left: anchor.left, width: Math.min(anchor.width, window.innerWidth - 32), maxHeight: "min(70vh, 32rem)" }}
              className={cn("z-50 overflow-y-auto rounded-xl border border-white/10 bg-card p-4 pr-10 shadow-2xl")}
            >
              <button
                ref={closeButton}
                type="button"
                onClick={close}
                aria-label="Close the synopsis"
                className="absolute right-2 top-2 rounded-md p-1 text-muted-foreground hover:bg-white/10 hover:text-foreground"
              >
                <X className="h-4 w-4" />
              </button>
              <p className="text-sm leading-relaxed text-foreground">{text}</p>
            </div>,
            document.body
          )
        : null}
    </div>
  );
}
