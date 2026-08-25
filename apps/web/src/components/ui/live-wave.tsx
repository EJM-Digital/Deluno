/**
 * LiveWave — a genuinely live, continuously scrolling waveform (#270).
 *
 * The dashboard's sparklines draw stored history: a fixed array, redrawn when
 * the data changes. This is the opposite — a rolling window of the *present*,
 * advancing on its own clock so the board reads as alive even while a value
 * holds steady. It samples whatever number it is given, eases toward it so a
 * step change arrives as a swell rather than a cliff, and scrolls the window
 * left at a constant rate.
 *
 * Canvas rather than SVG: this repaints ~30 times a second, and mutating a few
 * hundred SVG points that often is the one thing that would make the dashboard
 * feel heavy instead of fast.
 *
 * Honesty rules carried over from MetricChart: the window is real readings at a
 * real cadence, nothing is smoothed beyond the easing described above, and the
 * caller is expected to say the window is live rather than stored. With
 * `prefers-reduced-motion` the animation stops and the same samples are drawn
 * as a still chart — the data is identical, only the motion goes.
 */
import { useEffect, useRef } from "react";
import { cn } from "../../lib/utils";

export type WaveTone = "primary" | "success" | "warning" | "danger" | "info";

const TONE_VAR: Record<WaveTone, string> = {
  primary: "--primary",
  success: "--success",
  warning: "--warning",
  danger: "--destructive",
  info: "--info"
};

/** Readings held in the rolling window. At 30fps this is ~6 seconds of trace. */
const SAMPLES = 180;
/** How quickly the drawn value chases the real one. 0–1 per frame. */
const EASING = 0.12;

export function LiveWave({
  value,
  max,
  tone = "primary",
  height = 64,
  className,
  label
}: {
  /** The current reading. Any unit — the wave only needs it relative to `max`. */
  value: number;
  /**
   * Full-scale for the vertical axis. Omit to let the wave scale to the
   * highest reading in its own window, which suits an unbounded number like
   * throughput; pass a value for a bounded one like a percentage.
   */
  max?: number;
  tone?: WaveTone;
  height?: number;
  className?: string;
  /** Screen-reader description; the canvas itself carries no meaning. */
  label: string;
}) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const targetRef = useRef(value);
  const samplesRef = useRef<number[]>(new Array(SAMPLES).fill(0));
  const currentRef = useRef(0);

  targetRef.current = Number.isFinite(value) ? value : 0;

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const context = canvas.getContext("2d");
    if (!context) return;

    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    let frame = 0;
    let running = true;
    /** Advances every frame; drives the idle sweep. One full pass ≈ 3 seconds. */
    let phase = 0;

    // Read the tone from the stylesheet so the wave follows the theme, including
    // a theme switch while the page is open.
    const readColour = () => {
      const raw = getComputedStyle(canvas).getPropertyValue(TONE_VAR[tone]).trim();
      return raw ? `hsl(${raw})` : "hsl(210 100% 60%)";
    };

    const resize = () => {
      const ratio = window.devicePixelRatio || 1;
      const width = canvas.clientWidth || 1;
      canvas.width = Math.round(width * ratio);
      canvas.height = Math.round(height * ratio);
      context.setTransform(ratio, 0, 0, ratio, 0, 0);
    };

    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(canvas);

    const draw = () => {
      const width = canvas.clientWidth || 1;
      const colour = readColour();
      const samples = samplesRef.current;

      // Scale to the window's own peak unless the caller fixed one, with a
      // floor so an idle trace sits low rather than filling the card with noise.
      const peak = max ?? Math.max(...samples, 0.0001);
      const ceiling = peak <= 0 ? 1 : peak * 1.15;

      context.clearRect(0, 0, width, height);

      const step = width / (samples.length - 1);
      // A zero reading rests a few pixels clear of the bottom edge rather than
      // flush against it: an idle trace still has to be a visible line, or the
      // card reads as broken rather than quiet. The value itself is always
      // printed beside the wave, so the floor cannot be mistaken for throughput.
      const floor = 5;
      const pointY = (sample: number) => height - floor - (Math.min(sample, ceiling) / ceiling) * (height - floor - 4);

      context.beginPath();
      context.moveTo(0, pointY(samples[0]));
      for (let index = 1; index < samples.length; index += 1) {
        // Midpoint quadratic smoothing: enough to read as a wave, not enough to
        // invent a shape the readings do not have.
        const x = index * step;
        const previousX = (index - 1) * step;
        const midX = (previousX + x) / 2;
        context.quadraticCurveTo(previousX, pointY(samples[index - 1]), midX, (pointY(samples[index - 1]) + pointY(samples[index])) / 2);
      }
      context.lineTo(width, pointY(samples[samples.length - 1]));

      // Fill under the trace.
      context.save();
      context.lineTo(width, height);
      context.lineTo(0, height);
      context.closePath();
      const gradient = context.createLinearGradient(0, 0, 0, height);
      gradient.addColorStop(0, withAlpha(colour, 0.34));
      gradient.addColorStop(1, withAlpha(colour, 0));
      context.fillStyle = gradient;
      context.fill();
      context.restore();

      // The trace itself, redrawn as a stroke.
      context.beginPath();
      context.moveTo(0, pointY(samples[0]));
      for (let index = 1; index < samples.length; index += 1) {
        const x = index * step;
        const previousX = (index - 1) * step;
        const midX = (previousX + x) / 2;
        context.quadraticCurveTo(previousX, pointY(samples[index - 1]), midX, (pointY(samples[index - 1]) + pointY(samples[index])) / 2);
      }
      context.lineTo(width, pointY(samples[samples.length - 1]));
      context.strokeStyle = colour;
      context.lineWidth = 1.75;
      context.lineJoin = "round";
      context.lineCap = "round";
      context.stroke();

      // An idle window is a flat line, which is the truth but looks like a dead
      // panel. A highlight sweeping along it says "still watching" without
      // drawing a reading that did not happen — the value beside the wave still
      // says Idle, and the trace itself stays exactly where zero is.
      const idle = peak <= 0.0001;
      if (idle && !reduceMotion) {
        const centre = (phase % 1) * width;
        const sweep = context.createLinearGradient(centre - 70, 0, centre + 70, 0);
        sweep.addColorStop(0, withAlpha(colour, 0));
        sweep.addColorStop(0.5, colour);
        sweep.addColorStop(1, withAlpha(colour, 0));
        context.save();
        context.strokeStyle = sweep;
        context.lineWidth = 2.25;
        context.lineCap = "round";
        context.shadowColor = colour;
        context.shadowBlur = 8;
        context.beginPath();
        context.moveTo(Math.max(0, centre - 70), pointY(0));
        context.lineTo(Math.min(width, centre + 70), pointY(0));
        context.stroke();
        context.restore();
      }

      // The leading edge, glowing — where "now" is.
      const headY = pointY(samples[samples.length - 1]);
      context.save();
      context.shadowColor = colour;
      context.shadowBlur = 10;
      context.fillStyle = colour;
      context.beginPath();
      context.arc(width - 1.5, headY, 2.5, 0, Math.PI * 2);
      context.fill();
      context.restore();
    };

    const tick = () => {
      if (!running) return;
      phase += 1 / 180;
      currentRef.current += (targetRef.current - currentRef.current) * EASING;
      const samples = samplesRef.current;
      samples.shift();
      samples.push(currentRef.current);
      draw();
      frame = window.requestAnimationFrame(tick);
    };

    if (reduceMotion) {
      // Same readings, no motion: fill the window with the value and draw once.
      samplesRef.current = new Array(SAMPLES).fill(targetRef.current);
      currentRef.current = targetRef.current;
      draw();
    } else {
      frame = window.requestAnimationFrame(tick);
    }

    // A hidden tab should not be burning frames on a chart nobody is looking at.
    const onVisibility = () => {
      if (document.visibilityState === "visible") {
        if (!running && !reduceMotion) {
          running = true;
          frame = window.requestAnimationFrame(tick);
        }
      } else {
        running = false;
        window.cancelAnimationFrame(frame);
      }
    };
    document.addEventListener("visibilitychange", onVisibility);

    return () => {
      running = false;
      window.cancelAnimationFrame(frame);
      observer.disconnect();
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [height, max, tone]);

  return (
    <canvas
      ref={canvasRef}
      role="img"
      aria-label={label}
      style={{ height }}
      className={cn("block w-full", className)}
    />
  );
}

/** `hsl(a b% c%)` → `hsl(a b% c% / alpha)`, leaving other formats alone. */
function withAlpha(colour: string, alpha: number) {
  return colour.startsWith("hsl(") ? `${colour.slice(0, -1)} / ${alpha})` : colour;
}
