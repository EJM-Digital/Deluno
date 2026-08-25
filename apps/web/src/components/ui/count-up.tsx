/**
 * CountUp — a number that moves when it changes (#270).
 *
 * On a board that is mostly numbers, a value replacing itself between renders
 * is invisible; a value that travels tells you something just happened. The
 * animation is short and eased, and it never invents intermediate meaning — the
 * final frame is always the exact value passed in, and the accessible name is
 * that value throughout, so a screen reader is never read a tween.
 *
 * Under `prefers-reduced-motion` it simply prints the number.
 */
import { useEffect, useRef, useState } from "react";

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
  const startRef = useRef(0);

  useEffect(() => {
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      setDisplay(value);
      return;
    }

    const from = fromRef.current;
    if (from === value) return;

    startRef.current = 0;
    const step = (now: number) => {
      if (!startRef.current) startRef.current = now;
      const progress = Math.min(1, (now - startRef.current) / DURATION_MS);
      // Ease-out cubic: quick off the mark, settling rather than stopping.
      const eased = 1 - (1 - progress) ** 3;
      const current = from + (value - from) * eased;
      setDisplay(progress === 1 ? value : current);
      if (progress < 1) frameRef.current = window.requestAnimationFrame(step);
      else fromRef.current = value;
    };

    frameRef.current = window.requestAnimationFrame(step);
    return () => window.cancelAnimationFrame(frameRef.current);
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
