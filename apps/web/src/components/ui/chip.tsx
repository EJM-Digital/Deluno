import * as React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "../../lib/utils";

/**
 * Chip — the one status pill. Dot + short label, semantic tone.
 *
 * Its tones are `lib/status-tones.ts`'s `Tone`, which `StatusLed` also takes.
 * They used to be two vocabularies for the same five ideas — `muted` here and
 * `idle` there, `bad` here and `danger` there — so nothing could assert that a
 * state was coloured the same way in both. Do not choose a tone at the point of
 * use: look the state up in `STATUS_PRESENTATION` and pass what it says.
 * Use for health/state in list rows and drawer key/value lines.
 * (Badge remains for small square-ish tags such as media type.)
 */
const chipVariants = cva(
  "inline-flex h-6 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 text-[length:var(--type-caption)] font-semibold leading-none",
  {
    variants: {
      tone: {
        ok: "border-success/30 bg-success/10 text-success",
        warn: "border-warning/30 bg-warning/10 text-warning",
        bad: "border-destructive/30 bg-destructive/10 text-destructive",
        info: "border-info/30 bg-info/10 text-info",
        idle: "border-hairline bg-surface-2 text-muted-foreground"
      }
    },
    defaultVariants: { tone: "idle" }
  }
);

export interface ChipProps extends React.HTMLAttributes<HTMLSpanElement>, VariantProps<typeof chipVariants> {
  dot?: boolean;
}

export function Chip({ className, tone, dot = true, children, ...props }: ChipProps) {
  return (
    <span className={cn(chipVariants({ tone }), className)} {...props}>
      {dot ? <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-current" /> : null}
      {children}
    </span>
  );
}
