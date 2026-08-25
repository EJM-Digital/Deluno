/**
 * StatusLed — a status light that reads as a light (#270).
 *
 * A flat coloured dot states a status; this one carries a halo and a slow
 * breath, so a wall of them scans as an instrument panel and a fault catches
 * the eye from across the room. Colour is never the only signal — every use
 * sits beside its own label — and the motion is decorative, so it stops
 * entirely under `prefers-reduced-motion`.
 */
import { cn } from "../../lib/utils";

export type LedTone = "ok" | "warn" | "danger" | "info" | "idle";

const TONE: Record<LedTone, { core: string; glow: string }> = {
  ok: { core: "bg-success", glow: "bg-success/45" },
  warn: { core: "bg-warning", glow: "bg-warning/50" },
  danger: { core: "bg-destructive", glow: "bg-destructive/50" },
  info: { core: "bg-info", glow: "bg-info/45" },
  idle: { core: "bg-muted-foreground/45", glow: "bg-muted-foreground/20" }
};

export function StatusLed({
  tone,
  size = 8,
  /** A steady light for a steady state; pulse for something happening now. */
  pulse = false,
  className
}: {
  tone: LedTone;
  size?: number;
  pulse?: boolean;
  className?: string;
}) {
  const { core, glow } = TONE[tone];

  return (
    <span
      aria-hidden
      className={cn("relative inline-flex shrink-0 items-center justify-center", className)}
      style={{ width: size, height: size }}
    >
      {/* The halo sits behind and slightly larger, so the core keeps a hard edge. */}
      <span
        className={cn(
          "absolute inset-0 rounded-full blur-[3px]",
          glow,
          pulse && tone !== "idle" && "motion-safe:animate-ping motion-safe:[animation-duration:2.4s]"
        )}
      />
      <span className={cn("relative rounded-full", core)} style={{ width: size, height: size }} />
    </span>
  );
}
