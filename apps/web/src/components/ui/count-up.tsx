/**
 * CountUp — a number that moves when it changes (#270).
 *
 * On a board that is mostly numbers, a value replacing itself between renders
 * is invisible; a value that travels tells you something just happened. The
 * animation is short and eased, and it never invents intermediate meaning — the
 * final frame is always the exact value passed in, and the accessible name is
 * that value throughout, so a screen reader is never read a tween.
 *
 * Under `prefers-reduced-motion` it simply prints the number, and so it does
 * while the page is hidden or covered (#873): the browser pauses the frames a
 * tween needs, so a change made then would otherwise count up from a stale
 * value the moment the page is looked at again.
 */
import { useEffect, useRef, useState } from "react";
import { motionAllowed, onPageHidden } from "../../lib/page-motion";

const DURATION_MS = 520;

export function CountUp({
  value,
  format = (current: number) => current.toLocaleString(),
  className
}: {
  value: number;
  /** Applied to each frame; keep it cheap. */
  format?: (value: number) => string;
  className?: string;
}) {
  const [display, setDisplay] = useState(value);
  const fromRef = useRef(value);
  const frameRef = useRef(0);

  useEffect(() => {
    const from = fromRef.current;
    if (from === value) return;

    if (!motionAllowed()) {
      fromRef.current = value;
      setDisplay(value);
      return;
    }

    // Timed from the change, not from the first frame: if the browser delays
    // frames anyway, the first one that arrives late lands on the value.
    const start = performance.now();
    const step = (now: number) => {
      const progress = Math.min(1, Math.max(0, now - start) / DURATION_MS);
      // Ease-out cubic: quick off the mark, settling rather than stopping.
      const eased = 1 - (1 - progress) ** 3;
      const current = from + (value - from) * eased;
      setDisplay(progress === 1 ? value : current);
      if (progress < 1) frameRef.current = window.requestAnimationFrame(step);
      else fromRef.current = value;
    };

    frameRef.current = window.requestAnimationFrame(step);
    const stopWatching = onPageHidden(() => {
      window.cancelAnimationFrame(frameRef.current);
      fromRef.current = value;
      setDisplay(value);
    });
    return () => {
      window.cancelAnimationFrame(frameRef.current);
      stopWatching();
    };
  }, [value]);

  useEffect(() => {
    fromRef.current = display;
  }, [display]);

  return (
    <span className={className}>
      {/* The tween is decoration; the announced value is the real one. */}
      <span aria-hidden>{format(display)}</span>
      <span className="sr-only">{format(value)}</span>
    </span>
  );
}
