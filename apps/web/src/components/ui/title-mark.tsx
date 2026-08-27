import { cn } from "../../lib/utils";
import {
  TITLE_MARK_PRESENTATION,
  titleBarFraction,
  titleMark,
  type TitleMark
} from "../../lib/status-tones";

/**
 * The mark on a title: one dot, and one bar. See `DESIGN-001-title-marks.md`.
 *
 * The dot is the title itself — where it has got to on a four-rung ladder. The
 * bar is what you asked for beyond the title, which for a show is its episodes.
 * A half-grey dot means you are not monitoring it, and means nothing else.
 *
 * Nothing else appears on a title. Failures, machinery health and anything
 * genuinely blocked on a person live in Transfers, Activity and Needs You, which
 * is what frees red here for *Missing* and keeps amber meaningful everywhere
 * else.
 */

export interface TitleMarkInput {
  monitored: boolean;
  wantedStatus?: string | null;
  isTransferring?: boolean;
  airedEpisodeCount?: number;
  airedWithFileCount?: number;
  airedUpgradableCount?: number;
  nextAirDateUtc?: string | null;
}

/**
 * The dot.
 *
 * A half rather than a ring or a desaturation: a ring bled at 13px, and putting
 * three drained dots side by side makes them the same grey — desaturation
 * removes the very channel that told them apart.
 */
export function TitleMarkDot({
  item,
  size = 10,
  className
}: {
  item: TitleMarkInput;
  size?: number;
  className?: string;
}) {
  const mark: TitleMark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;
  const label = half ? `${presentation.label} · not monitored` : presentation.label;

  return (
    <span
      role="img"
      aria-label={label}
      title={half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint}
      className={cn(
        "inline-block shrink-0 rounded-full ring-1 ring-background/90",
        // The half is a gradient rather than two elements, so the dot stays one
        // shape at every size and keeps a hard edge down the middle.
        half ? "bg-[linear-gradient(90deg,currentColor_0_50%,hsl(var(--mark-idle))_50%_100%)]" : presentation.dot,
        half && presentation.dot.replace("bg-", "text-"),
        className
      )}
      style={{ width: size, height: size }}
    />
  );
}

/**
 * The bar, on the bottom edge of a poster.
 *
 * Green up to what you have, red for the rest. Asked for nothing — a film, or a
 * show with no aired episodes — and it does not appear at all rather than
 * drawing an empty claim.
 */
export function TitleMarkBar({ item, className }: { item: TitleMarkInput; className?: string }) {
  const fraction = titleBarFraction(item);
  if (fraction === null) return null;

  const held = item.airedWithFileCount ?? 0;
  const aired = item.airedEpisodeCount ?? 0;
  const percent = Math.round(fraction * 100);

  return (
    <span
      role="img"
      aria-label={`${held} of ${aired} aired episodes`}
      title={`${held} of ${aired} aired episodes`}
      className={cn("absolute inset-x-0 bottom-0 z-10 block h-1", className)}
      style={{
        background: `linear-gradient(to right, hsl(var(--success)) 0 ${percent}%, hsl(var(--destructive)) ${percent}% 100%)`
      }}
    />
  );
}

/** The dot with its name beside it, for a list row or a detail header. */
export function TitleMarkLabel({ item, className }: { item: TitleMarkInput; className?: string }) {
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;

  return (
    <span className={cn("inline-flex items-center gap-1.5 whitespace-nowrap", className)}>
      <TitleMarkDot item={item} size={8} />
      <span>{presentation.label}</span>
      {half ? <span className="text-muted-foreground">· not monitored</span> : null}
    </span>
  );
}
