import { cn } from "../../lib/utils";
import {
  TITLE_BAR_SEGMENTS,
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

/**
 * The dot's diameter on a small poster, and therefore in the legend.
 *
 * One constant because the legend row exists to teach the shelf: a legend whose
 * dots are a different size from the dots they explain is doing the job at half
 * strength, and two hard-coded numbers would drift the first time either moved.
 */
export const MARK_DOT_SIZE = 13;

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
  subtitleLanguagesSettled?: number;
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
        // No ring. It was there to stop "a dark mark disappearing into dark
        // artwork", and nothing in the ladder is dark any more — red 62%, green
        // 52%, gold 58%, violet 72%. At 2px on a 13px dot it was nearly a third
        // of the diameter and read as a grey fringe; at 1px it was still an
        // outline nobody asked for. The colours carry themselves.
        "inline-block shrink-0 rounded-full",
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
        // Quality met is gold leaf rather than a flat fill, and it glints. It is
        // the only rung that means Deluno has finished; the other four all mean
        // it is still working. `canBeHalf` is false for it, so this can never
        // fight the half-grey gradient above.
        !half && presentation.sheen,
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
 * **No languages asked for, no bar.** DESIGN-001 drew a grey one instead, to
 * keep "the shelf's shape" so nothing would be relaid out when the numbers
 * started arriving. That reason does not survive reading the CSS: the bar is
 * `absolute ... bottom-0`, painted over the poster, and takes no layout space —
 * adding or removing it re-lays out nothing. So the grey stripe was bought with
 * a benefit that never existed, and paid for with a mark on every poster in the
 * library that says nothing at all. The bar appears when it has something to
 * say.
 */
export function TitleMarkBar({ item, className }: { item: TitleMarkInput; className?: string }) {
  const bar = titleBar(item);

  if (bar.wanted <= 0) {
    return null;
  }

  const heldPercent = Math.round(Math.min(1, Math.max(0, bar.held / bar.wanted)) * 100);
  const settledPercent = Math.round(Math.min(1, Math.max(0, bar.settled / bar.wanted)) * 100);

  // Three numbers, and the label says all three rather than only the sum. "2 of
  // 2" over a subtitle that might be forty seconds out is exactly the claim this
  // ladder exists to stop making — and this label is all a screen reader gets.
  const label = bar.settled === bar.held
    ? `${bar.held} of ${bar.wanted} ${bar.noun}`
    : `${bar.held} of ${bar.wanted} ${bar.noun}, ${bar.settled} matched to this release`;

  return (
    <span
      role="img"
      aria-label={label}
      title={label}
      className={cn("absolute inset-x-0 bottom-0 z-10 block h-1", className)}
      style={{ background: titleBarGradient(settledPercent, heldPercent) }}
    />
  );
}

/**
 * The gradient the bar paints with.
 *
 * Three stops, left to right, in the order a title climbs: gold for the
 * languages Deluno has finished with, green for the ones you have and it is
 * still improving, red for the ones you do not have.
 *
 * The colours come from `TITLE_BAR_SEGMENTS`, never from `--success` and
 * `--destructive` written in here: the legend names the same three, and two
 * places naming one set is how they drift.
 */
function titleBarGradient(settledPercent: number, heldPercent: number): string {
  const [done, ready, missing] = TITLE_BAR_SEGMENTS;
  const colour = (mark: TitleMark) => `hsl(var(${TITLE_MARK_PRESENTATION[mark].cssVar}))`;

  return "linear-gradient(to right, "
    + `${colour(done.mark)} 0 ${settledPercent}%, `
    + `${colour(ready.mark)} ${settledPercent}% ${heldPercent}%, `
    + `${colour(missing.mark)} ${heldPercent}% 100%)`;
}

/**
 * The legend for the bar — [#327](https://github.com/jampat000/Deluno/issues/327).
 *
 * <b>On the chip row, after Upcoming, behind a divider.</b> A first attempt put
 * it in the View drawer, on #327's own argument that a second row of
 * chip-shaped things which filtered nothing would read as broken. James:
 * <i>"why cant the subtitle bar be up the top next to upcoming? with a divider?
 * it should not be in view cause nothing else is in there"</i> — right on both
 * halves. That drawer is switches, so a legend was the only thing in it that was
 * not one; and this row is already where a reader learns what a colour means.
 * The answer to "it must not look like a filter" was a divider, not a different
 * room.
 *
 * So these entries deliberately have <b>no count and no click</b>. That, plus
 * the rule between them and the chips, is what says they explain rather than
 * narrow.
 *
 * The swatch is a short strip at the bar's own height rather than a dot,
 * because a dot is what the chips beside it already use for the other mark. The
 * two must not be mistaken for each other.
 */
export function TitleMarkBarLegend({ className }: { className?: string }) {
  return (
    <div
      className={cn("flex items-center gap-2.5", className)}
      title={"The bar on a poster's bottom edge: the subtitle languages this shelf asked for. "
        + "Counted over the files a title has, so a show you have downloaded nothing of shows no bar."}
    >
      <span className="text-[length:var(--library-toolbar-size)] font-medium text-muted-foreground">
        Subtitles
      </span>
      {TITLE_BAR_SEGMENTS.map((segment) => (
        <span
          key={segment.mark}
          className="flex items-center gap-1.5 text-[length:var(--library-toolbar-size)] font-medium text-muted-foreground"
        >
          {/* Colour is never the only carrier (#318): the word is always beside it. */}
          <span
            aria-hidden
            className={cn("h-1 w-4 shrink-0 rounded-full", TITLE_MARK_PRESENTATION[segment.mark].dot)}
          />
          <span>{segment.label}</span>
        </span>
      ))}
    </div>
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
