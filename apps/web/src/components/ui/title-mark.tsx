import { cn } from "../../lib/utils";
import {
  TITLE_MARK_PRESENTATION,
  titleBar,
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
  hasFile?: boolean;
  /**
   * Episode counts. They decide a show's *dot* — the lowest rung any aired
   * episode is on — and `airedWithFileCount` also says how many files a show
   * has, which is what its subtitle bar is measured over. They are no longer
   * drawn on a poster themselves; the show's own page carries them.
   */
  airedEpisodeCount?: number;
  airedWithFileCount?: number;
  airedUpgradableCount?: number;
  nextAirDateUtc?: string | null;
  /** Zero until Subber (#301). See `titleBar`. */
  subtitleLanguagesWanted?: number;
  subtitleLanguagesHeld?: number;
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
  size = 12,
  /**
   * Hides the dot from assistive technology, for when it sits inside something
   * that already carries the name — otherwise a screen reader announces
   * "Missing" twice for one mark.
   */
  decorative = false,
  className
}: {
  item: TitleMarkInput;
  size?: number;
  decorative?: boolean;
  className?: string;
}) {
  const mark: TitleMark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;
  const label = half ? `${presentation.label} · not monitored` : presentation.label;

  return (
    <span
      {...(decorative
        ? { "aria-hidden": true }
        : { role: "img", "aria-label": label, title: half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint })}
      className={cn(
        // A ring, not a border: it keeps the dot the same diameter at
        // every size and stops a dark mark disappearing into dark artwork.
        "inline-block shrink-0 rounded-full ring-2 ring-black/45",
        // The half is a gradient rather than two elements, so the dot stays one
        // shape at every size and keeps a hard edge down the middle.
        // `currentColor` for the filled half, so the gradient is written once
        // rather than five times. The colour comes from the table's own `text`
        // class — spelled out there, because a class built here with
        // `.replace("bg-", "text-")` is invisible to Tailwind and gets purged.
        // That is exactly what happened to Upcoming: its half rendered with no
        // colour at all, and nothing failed.
        half ? "bg-[linear-gradient(90deg,currentColor_0_50%,hsl(var(--mark-idle))_50%_100%)]" : presentation.dot,
        half && presentation.text,
        className
      )}
      style={{ width: size, height: size }}
    />
  );
}

/**
 * The bar, on the bottom edge of a poster: the subtitle languages you asked for.
 *
 * Green up to what you have, red for the rest — and the same question on both
 * shelves. It used to count aired episodes on a show, so the identical strip of
 * pixels meant one thing on Movies and another on TV, and a show had nowhere to
 * show its subtitles at all. See `titleBar`.
 *
 * A title that asked for nothing gets a grey bar rather than no bar. It claims
 * nothing, and the shelf keeps its shape — so nothing has to be relaid out the
 * day Subber (#301) starts filling subtitle languages in.
 */
export function TitleMarkBar({ item, className }: { item: TitleMarkInput; className?: string }) {
  const bar = titleBar(item);

  if (bar.wanted <= 0) {
    return (
      <span
        aria-hidden
        title="No subtitle languages asked for."
        className={cn("absolute inset-x-0 bottom-0 z-10 block h-1 bg-mark-idle/50", className)}
      />
    );
  }

  const percent = Math.round(Math.min(1, Math.max(0, bar.held / bar.wanted)) * 100);
  const label = `${bar.held} of ${bar.wanted} ${bar.noun}`;

  return (
    <span
      role="img"
      aria-label={label}
      title={label}
      className={cn("absolute inset-x-0 bottom-0 z-10 block h-1", className)}
      style={{
        background: `linear-gradient(to right, hsl(var(--success)) 0 ${percent}%, hsl(var(--destructive)) ${percent}% 100%)`
      }}
    />
  );
}

/**
 * The dot with its name beside it, for a list row or a detail header.
 *
 * One image carrying the whole mark, rather than a dot and some loose text: a
 * screen reader gets "Missing · not monitored" once, in the order a sighted
 * reader gets it, instead of a decorative dot followed by two unrelated
 * fragments. The dot inside is hidden for the same reason.
 */
export function TitleMarkLabel({ item, className }: { item: TitleMarkInput; className?: string }) {
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;
  const label = half ? `${presentation.label} · not monitored` : presentation.label;

  return (
    <span
      role="img"
      aria-label={label}
      title={half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint}
      className={cn("inline-flex items-center gap-1.5 whitespace-nowrap", className)}
    >
      <TitleMarkDot item={item} size={10} decorative />
      <span>{presentation.label}</span>
      {half ? <span className="text-muted-foreground">· not monitored</span> : null}
    </span>
  );
}

/**
 * The dot with its name, for a poster large enough to carry the word.
 *
 * At small sizes the dot alone has to do the work — there is no room, and a
 * shelf of small posters is read by colour anyway. From medium up there is room
 * for the word, and a colour nobody has been taught yet should not be the only
 * thing carrying the meaning.
 */
export function TitleMarkChip({ item, className }: { item: TitleMarkInput; className?: string }) {
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;

  return (
    <span
      role="img"
      aria-label={half ? `${presentation.label} · not monitored` : presentation.label}
      title={half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border border-white/15 bg-black/55 px-2 py-0.5",
        "text-[length:var(--library-badge-size)] font-bold uppercase tracking-wider text-white backdrop-blur-md",
        className
      )}
    >
      <TitleMarkDot item={item} size={9} decorative />
      {presentation.label}
    </span>
  );
}
