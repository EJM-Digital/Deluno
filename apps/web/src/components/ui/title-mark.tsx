import { cn } from "../../lib/utils";
import {
  TITLE_BAR_SEGMENTS,
  TITLE_MARK_PRESENTATION,
  titleBar,
  titleProgress,
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
 * The dot's diameter wherever one is drawn beside a title's name — a list row's
 * status chip today, and whatever joins it.
 *
 * It used to govern the shelf's legend chips as well, on the reasoning that a
 * legend whose dots differ in size from the dots they explain is working at half
 * strength. That reasoning is intact; its premise is not. A poster has not
 * carried a dot since the state became a bar across its top, so the legend now
 * draws {@link MarkStrip} and this governs the dots that are still drawn.
 */
export const MARK_DOT_SIZE = 13;

/**
 * The swatch a legend uses for a mark: a short strip, at the subtitle bar's own
 * height.
 *
 * **Why a strip and not a dot.** The row above the shelf explains what is drawn
 * on the posters below it, and nothing down there is a dot any more — the state
 * is a bar across the poster's top and the subtitles are a bar across its
 * bottom. A dot in the legend was a shape a reader could not then find.
 *
 * **Why the subtitle bar's height rather than the state bar's.** James: *"its a
 * thicker line than subtitles or thinner or just use the same as subs actually
 * because the thicker line doesnt exist until someone clicks on quality and its
 * just a visual reference anyway"*. Right — the state bar is thin at `5px` and
 * grows to a full labelled bar the moment the Quality switch goes on, so there
 * is no one height for the legend to match. A swatch that changed size with a
 * display switch would be claiming that the switch changed what the colour
 * means. One height for every swatch on the row, and it is the one height on a
 * poster that never moves.
 */
export function MarkStrip({
  mark,
  /**
   * Whether to draw the mark's sheen. On everywhere gold is currently drawn:
   * one gold, one treatment, wherever "Deluno has finished" is said. It stays a
   * parameter rather than being folded into the table because a swatch on a
   * dense list row is a place this would be noise, and that is a judgement about
   * where it is drawn rather than about the mark.
   */
  sheen = false,
  className
}: { mark: TitleMark; sheen?: boolean; className?: string }) {
  const presentation = TITLE_MARK_PRESENTATION[mark];

  return (
    <span
      aria-hidden
      className={cn(
        "h-1 w-4 shrink-0 rounded-full",
        presentation.dot,
        sheen && presentation.sheen,
        className
      )}
    />
  );
}

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

  // The dot carries the rung and nothing else.
  //
  // <b>How far through a show you are was briefly drawn into it</b> — an arc
  // filled to the episodes you hold. Sonarr's idea, moved off the poster's edge
  // because that edge belongs to the subtitle bar (James: <i>"adding a bar isnt
  // a good idea — the bar is strictly for subtitles"</i>). It was correct and
  // illegible: a 15% arc on a nine-pixel dot is about one pixel wide, and at 0%
  // the whole dot washed out to a smudge. Sonarr uses a bar because a bar is
  // what a fraction needs.
  //
  // So the fraction is text now, on the chip — see TitleMarkChip.
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
 * The state, as a bar across the top of a poster.
 *
 * <p>It replaced a dot in the corner. A dot is nine pixels and it was carrying
 * the most important fact on the card, with the tier you hold as a separate
 * pill at the other end — three things to learn, two of them tiny. James, after
 * looking at four rendered treatments: <i>"I like a combination of 1 and 2...
 * if quality is not ticked it goes to the small bar, if its ticked it goes to
 * the big bar with the quality or status"</i>.</p>
 *
 * <p><b>The label decides the size.</b> No label and it is a thin strip of
 * colour; a label and it is a full bar carrying the words. That is one control
 * — the Quality switch — doing one thing, rather than two switches arguing over
 * one corner.</p>
 *
 * <p>The colour comes from <c>titleMark</c>, the same source the dot used, the
 * legend reads and the detail page reads. Gold means Deluno has finished with
 * it and glints to say so; the other rungs are flat because they are all still
 * in progress. Half-width for a title Deluno is not watching, exactly as the
 * dot went half.</p>
 *
 * <p>The bottom edge belongs to the subtitle bar and is not touched, so the two
 * book-end the artwork rather than competing for one end of it.</p>
 */
export function TitleMarkTopBar({
  item,
  label,
  className
}: {
  item: TitleMarkInput;
  /** The tier you hold, or the state's own word. Null draws the thin strip. */
  label?: string | null;
  className?: string;
}) {
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;

  const description = half
    ? `${presentation.label} · not monitored`
    : presentation.label;

  // Shows only. `titleProgress` answers 0 or 1 for anything without episode
  // counts, and a film reading 0 would be a poster with no state on it.
  const tracksEpisodes = typeof item.airedEpisodeCount === "number" && item.airedEpisodeCount > 0;
  const fillPercent = tracksEpisodes ? Math.round(titleProgress(item) * 100) : 100;

  return (
    <div
      role="img"
      // The quality label is the state's own word when there is no file to
      // name, so a missing title announced "Missing · Missing" until this
      // deduplicated them.
      aria-label={[label === description ? null : label, description, tracksEpisodes
        ? `${Math.min(Math.max(0, item.airedWithFileCount ?? 0), item.airedEpisodeCount!)} of ${item.airedEpisodeCount} aired episodes on disk`
        : null].filter(Boolean).join(" · ")}
      title={half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint}
      className={cn(
        "absolute inset-x-0 top-0 z-10 flex items-center justify-center overflow-hidden",
        label ? "px-2 py-0.5" : "h-[5px]",
        className
      )}
    >
      {/*
        The fill is its own layer because `.mark-grail` sets `position:
        relative` and beats a Tailwind `absolute` on the same element — it did
        exactly that twice while the quality pill was being built, once dropping
        it out of the poster and once collapsing it to nothing, both times
        rendering the right gradient invisibly.

        **And its width is how far through the show you are.** It was always
        100%, so a series holding three of twenty aired episodes drew the same
        full red bar as one holding none — James: *"the red bar at the top
        doesnt track episodes properly"*. `titleProgress` has computed exactly
        this fraction since DESIGN-001 and nothing has ever drawn it: declared,
        never populated, and invisible because a full bar is what a full bar
        looks like.

        A film is not partway through itself, so it keeps a solid bar. Filling
        one by `hasFile` would leave every missing film with an empty strip and
        no state on the poster at all.
      */}
      <span
        aria-hidden="true"
        className="absolute inset-0"
        // The track is the mark's own colour, dimmed — not grey.
        //
        // Grey was the first attempt and it cost the thing that matters: a show
        // holding none of its aired episodes filled to 0%, so five of six TV
        // posters had no colour on them at all, one commit after the state mark
        // was made mandatory. Sonarr does not do that either — its bar is the
        // state colour throughout, with the filled part brighter.
        //
        // `surfaceVar` where a mark has one, so gold dims to gold.
        //
        // 0.55 rather than the 0.3 Sonarr uses, because Sonarr's bar sits on a
        // white page and this one sits on artwork: at 0.3 the label washed out
        // and the strip read as a smear rather than a colour. The filled part
        // is still obviously brighter, which is the only thing the ratio has to
        // preserve.
        style={{
          background: `hsl(var(${presentation.surfaceVar ?? presentation.cssVar}) / 0.55)`
        }}
      >
        <span
          className={cn(
            "block h-full",
            half
              ? "bg-[linear-gradient(90deg,currentColor_0_50%,hsl(var(--mark-idle))_50%_100%)]"
              : presentation.dot,
            half && presentation.text,
            !half && presentation.sheen
          )}
          style={{ width: `${fillPercent}%` }}
        />
      </span>

      {label ? (
        // Dark text: every rung's fill is a light colour — red 62%, green 52%,
        // gold 58% — and white would vanish into the gold.
        <span className="relative truncate text-[length:var(--library-badge-size)] font-bold uppercase tracking-wider text-black/85">
          {label}
        </span>
      ) : null}
    </div>
  );
}

/**
 * How many aired episodes you hold, as a filled bar with the count on it.
 *
 * <p><b>Sonarr's, deliberately.</b> Looked at on James's own instance: its list
 * draws the episode count as a proportionally filled bar with <c>16 / 16</c>
 * printed on it, coloured by the show's state — and its poster wall draws a
 * thin line with no numbers, with the count behind an opt-in that ships off.
 * James: <i>"I want to mimic what sonarr is doing"</i>. Deluno's list had the
 * count as plain text on a status chip and no bar at all, so this is the half
 * that was missing.</p>
 *
 * <p>The colour is the mark's, from the one table, so a row and the poster it
 * corresponds to cannot disagree. Gold takes dark text for the same reason the
 * state bar does: every rung on the ladder is a light colour and white would
 * vanish into it.</p>
 *
 * <p>A film has no fraction — it is here or it is not — so this draws nothing
 * and the caller shows a dash. That is not the same as zero of zero, which is
 * what a show whose episodes Deluno has not learned about yet has.</p>
 */
export function EpisodeProgressBar({ item, className }: { item: TitleMarkInput; className?: string }) {
  const aired = item.airedEpisodeCount;
  if (typeof aired !== "number" || aired <= 0) {
    return null;
  }

  const held = Math.min(Math.max(0, item.airedWithFileCount ?? 0), aired);
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const percent = Math.round((held / aired) * 100);
  const label = `${held} of ${aired} aired episodes on disk`;

  return (
    <span
      role="img"
      aria-label={label}
      title={label}
      className={cn(
        "relative inline-flex h-[18px] min-w-[58px] items-center justify-center overflow-hidden rounded",
        "bg-mark-idle/70 text-[length:var(--library-badge-size)] font-bold tabular-nums",
        className
      )}
    >
      {/*
        The fill is its own layer rather than a background on the box, because
        `.mark-grail` sets `position: relative` and beats a Tailwind `absolute`
        on the same element — it has dropped an element out of its parent twice
        in this file already.
      */}
      <span aria-hidden className="absolute inset-y-0 left-0" style={{ width: `${percent}%` }}>
        <span className={cn("block h-full w-full", presentation.dot, presentation.sheen)} />
      </span>
      <span className={cn("relative px-1.5", percent >= 55 ? "text-black/85" : "text-foreground")}>
        {held} / {aired}
      </span>
    </span>
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
export function titleBarGradient(settledPercent: number, heldPercent: number): string {
  const [done, ready, missing] = TITLE_BAR_SEGMENTS;
  // `surfaceVar` where a mark has one, because this bar is a surface on
  // artwork rather than a word on a page. Only gold differs, and it has to:
  // its semantic value is the Quality met count's text colour, dark by design
  // in the light theme, and a dark yellow painted onto a poster is brown.
  const colour = (mark: TitleMark) => {
    const presentation = TITLE_MARK_PRESENTATION[mark];
    return `hsl(var(${presentation.surfaceVar ?? presentation.cssVar}))`;
  };

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
 * The swatch is {@link MarkStrip}, the same strip the chips beside it wear —
 * one shape for every swatch on the row, because both halves of the row now
 * explain a bar.
 *
 * <b>An Episodes entry has gone with it.</b> It was behind a `showEpisodes`
 * prop nothing ever passed, drawing a swatch for a strip posters stopped
 * carrying when episode counts moved to a show's own page: a legend for
 * something that cannot appear, which is the defect a chip that can never match
 * is. Declared, never populated, and no test could see it.
 */
export function TitleMarkBarLegend({ className }: { className?: string }) {
  return (
    <div className={cn("flex items-center gap-2.5", className)}>
      <span
        className="text-[length:var(--library-toolbar-size)] font-medium text-muted-foreground"
        title={"The strip on a poster's bottom edge: the subtitle languages this shelf asked for. "
          + "Counted over the files a title has, so a show you have downloaded nothing of shows no bar."}
      >
        Subtitles
      </span>
      {TITLE_BAR_SEGMENTS.map((segment) => (
        <span
          key={segment.mark}
          className="flex items-center gap-1.5 text-[length:var(--library-toolbar-size)] font-medium text-muted-foreground"
        >
          {/*
            Colour is never the only carrier (#318): the word is always beside
            it.

            The sheen is on, and it did not used to be: the argument was that
            the subtitle bar is a flat gradient, so a glinting swatch would show
            a treatment the bar never wears. That was true when gold here was
            the semantic colour. It is the leaf now — the same surface the state
            bar is painted with — so a reader sees one gold in one treatment
            wherever "Deluno has finished" is being said, which is the whole
            point of a legend.
          */}
          <MarkStrip mark={segment.mark} sheen />
          {/*
            The ladder's own word, not a synonym.
            James: *"users need to also be able to distinguish between done and
            ready for subtitles its a little ambiguous compared to the status of
            the files."* Right — the bar had invented "Done" and "Ready" for
            rungs the dot already calls "Quality met" and "Upgradable", so a
            reader had two vocabularies for one ladder and no way to line them
            up. DESIGN-002 says the bar *is* a miniature of that ladder; it now
            borrows its words as well as its colours.
          */}
          <span>{TITLE_MARK_PRESENTATION[segment.mark].label}</span>
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

  // Shows only, and only once Deluno knows what has aired. Zero of an unknown
  // total is not a fraction, and printing one would claim knowledge it has not
  // got.
  const aired = item.airedEpisodeCount;
  const episodes = typeof aired === "number" && aired > 0
    ? `${Math.min(Math.max(0, item.airedWithFileCount ?? 0), aired)}/${aired}`
    : null;

  const label = half ? `${presentation.label} · not monitored` : presentation.label;

  return (
    <span
      role="img"
      aria-label={episodes ? `${label} · ${episodes} aired episodes` : label}
      title={half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border border-white/15 bg-black/55 px-2 py-0.5",
        "text-[length:var(--library-badge-size)] font-bold uppercase tracking-wider text-white backdrop-blur-md",
        className
      )}
    >
      {/*
        The same diameter as the chips above the shelf, and the dot on a small
        poster — one constant, three places. It was 9 here against their 13, so
        the mark a reader learns in the legend was not quite the mark they then
        looked for on the artwork.
      */}
      <TitleMarkDot item={item} size={MARK_DOT_SIZE} decorative />
      {presentation.label}
      {episodes ? (
        <>
          {/*
            How far through a show you are, in the one place on a poster with
            room for it.

            Why text and not a mark: the bar belongs to subtitles, and an arc on
            a nine-pixel dot is a pixel wide and unreadable. A fraction is a
            number, and numbers read as numbers. It also does the thing the
            ladder cannot — three of twenty and none of eighty-seven are both
            Missing and both red, and only one of them is nearly done.

            A film has no fraction: it is here or it is not, and the word beside
            this has already said which.
          */}
          <span aria-hidden className="text-white/40">&middot;</span>
          <span className="font-semibold tabular-nums text-white/80">{episodes}</span>
        </>
      ) : null}
    </span>
  );
}
