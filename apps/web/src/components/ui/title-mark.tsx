import { cn } from "../../lib/utils";
import {
  TITLE_BAR_SEGMENTS,
  TITLE_MARK_PAINT,
  TITLE_MARK_PRESENTATION,
  UNMONITORED_PAINT,
  titleBar,
  titleProgress,
  titleMark,
  type TitleMark
} from "../../lib/status-tones";
import { cardDesign } from "../../lib/card-design";
import type { MediaType } from "../../lib/media-types";

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
   * The shelf this legend sits above.
   *
   * **A legend must be painted in the colours it is explaining.** Once a shelf
   * adopts DESIGN-006 its cards are painted from the bar *surfaces*, which are a
   * different set from the page-text colours this swatch used — so the row above
   * the shelf was explaining one palette while the posters below it drew
   * another. James: *"you havent executed it exactly as per the spec"*. Measured
   * on the rig: Missing rgb(239,77,77) in the legend against rgb(192,17,28) on
   * the card, and every other rung likewise.
   *
   * Absent, or a shelf that has not adopted the bars, keeps the semantic swatch.
   * The adopted movie and TV shelves pass their medium so this uses the same
   * surface token as the bar below it.
   */
  type,
  /**
   * Whether to draw the mark's sheen. On everywhere gold is currently drawn:
   * one gold, one treatment, wherever "Deluno has finished" is said. It stays a
   * parameter rather than being folded into the table because a swatch on a
   * dense list row is a place this would be noise, and that is a judgement about
   * where it is drawn rather than about the mark.
   */
  sheen = false,
  /**
   * Whether the title this swatch stands for is being watched.
   *
   * **Unmonitored is the override, and it overrides here too.** The card paints
   * an unmonitored title one flat grey — fill and track both — and this strip
   * did not, so a compact list row drew Missing red for a title whose poster
   * two clicks away drew it grey. Measured on the rig: rgb(192,17,28) in the
   * row against rgb(108,114,127) on the card.
   *
   * Defaults to true because a LEGEND is not a title: the chips above a shelf
   * explain what each colour means, and "Unmonitored" is its own chip beside
   * them rather than a state the other five can be in.
   */
  monitored = true,
  className
}: { mark: TitleMark; type?: MediaType; sheen?: boolean; monitored?: boolean; className?: string }) {
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const paintsBars = type ? cardDesign(type).bars : false;
  const paint = monitored ? TITLE_MARK_PAINT[mark] : UNMONITORED_PAINT;

  return (
    <span
      aria-hidden
      className={cn(
        "h-1 w-4 shrink-0 rounded-full",
        presentation.stateClass,
        !paintsBars && presentation.dot,
        // No gold leaf on a title nothing is watching: the grail says "Deluno
        // has finished", and it has not been asked to start.
        sheen && monitored && presentation.sheen,
        className
      )}
      // `backgroundColor`, NOT the `background` shorthand.
      //
      // The shorthand resets `background-image`, which is the whole of
      // `.mark-grail` — so painting the legend from the card's surfaces
      // silently cancelled the gold leaf on Quality met, and the one rung with
      // a treatment of its own went flat while the card's own bar stayed gold.
      // That is the exact failure this swatch exists to prevent: a legend
      // explaining a palette its shelf does not draw. The longhand lets the
      // surface be the colour underneath and the grail gradient sit on top, the
      // same layering the bar itself uses.
      style={paintsBars ? { backgroundColor: `hsl(var(${paint.surface}))` } : undefined}
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
  const label = half ? `${presentation.label} · unmonitored` : presentation.label;

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
        "relative inline-flex shrink-0 items-center justify-center overflow-hidden rounded-full",
        presentation.stateClass,
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
    >
      <span aria-hidden data-glyph={presentation.glyph} className="title-mark-glyph pointer-events-none absolute inset-0 flex items-center justify-center text-[0.58em] font-black leading-none text-white" />
    </span>
  );
}

/**
 * What colour a word sitting on one of these bars has to be.
 *
 * <p>The state bar's label was a fixed <c>text-black/85</c>, on the reasoning
 * that every rung's fill is a light colour and white would vanish into the
 * gold. That was true of a bar that was entirely fill. It stopped being true
 * the moment the bar started tracking episode progress: a show holding three of
 * twenty is 15% bright colour and 85% dimmed track over dark artwork, and black
 * on that is unreadable. James: <i>"fix the washing out, if the text is the
 * problem change the colour to white or something"</i>.</p>
 *
 * <p>So the word follows the surface under it rather than the mark. Mostly
 * filled means a light ground and dark text; mostly track means a dark ground
 * and white text, with a shadow because the ground behind it is artwork and
 * artwork can be any colour. One rule, used by both bars, because they are the
 * same problem twice.</p>
 */
function labelTone(fillPercent: number): string {
  return fillPercent >= 55
    ? "text-black/85"
    // Tailwind cannot parse commas inside an arbitrary value, so the shadow is
    // spelled with underscores and a slash.
    : "text-white [text-shadow:0_1px_2px_rgb(0_0_0_/_0.65)]";
}

/**
 * The state, as a bar across the top of a poster.
 *
 * <p><b>It carries no words.</b> It used to grow into a full bar and print the
 * tier you hold, and that is what three rounds of wash-out were about: a word
 * on a bar whose ground changes with the episode count cannot be given a colour
 * that works. James, after four rendered treatments: <i>"lets do A"</i> — the
 * bar says the state, the corner says the count, and nothing sits on artwork.
 * Quality left the poster with it; it is on the list row, in the drawer and on
 * the detail page already.</p>
 *
 * <p><b>Full-strength colours, nothing translucent.</b> <i>"ensure colours are
 * full and not transparent or washed out"</i>. On the active shelves the held
 * portion is green, a fully held Quality met title is gold, and the unfilled
 * remainder is solid Missing red. It is the part you do not have yet, not a
 * dimmed version of the state. An unmonitored title overrides both with one
 * flat grey, so grey has one meaning and no monitored coverage disappears into
 * it.</p>
 *
 * <p>The width is how far through the show you are. A film is not partway
 * through itself, so its bar is solid.</p>
 */
/* ═══════════════ THE CARD'S BARS — DESIGN-006 ═══════════════ */

/**
 * One bar, drawn once, for whichever shelf asks for it.
 *
 * <p><b>The label is drawn twice and each copy is clipped to its own half.</b> A
 * copy in the fill's colour clipped to the fill, and a copy in the track's
 * colour clipped to the complement. Every glyph is therefore coloured for the
 * ground directly beneath it, so a bar 15% full has 15% of its label in one
 * colour and 85% in the other. This is how Sonarr and Radarr keep a word legible
 * on a bar whose ground moves — read out of Sonarr's own DOM rather than
 * recalled.</p>
 *
 * <p><b>Two ways to get it wrong, both of which happened here.</b> Sizing the
 * front layer to the fill and centring the text inside it centres the label on
 * the <i>fill</i> rather than the <i>bar</i>, so it slides sideways as the bar
 * fills. And leaving the back layer unclipped makes a fully-filled bar paint the
 * identical glyphs twice, compositing every antialiased edge pixel into an
 * opaque one, so the text thickens and glows. James, twice, before the cause was
 * found: <i>"almost like its overexposed"</i>. It was a double exposure.</p>
 */
function TwoToneBar({
  fill,
  fillColour,
  onFill,
  trackColour,
  onTrack,
  lead,
  label,
  title,
  ariaLabel,
  edge,
  pattern
}: {
  /** 0 to 100. */
  fill: number;
  fillColour: string;
  onFill: string;
  trackColour: string;
  onTrack: string;
  lead?: string;
  label?: string;
  title: string;
  ariaLabel: string;
  /** Stable non-colour texture for the title-state bar. */
  pattern?: string;
  /**
   * Which edge of the artwork this bar pins to.
   *
   * **It must be positioned, not laid out in flow.** The artwork box is
   * `relative aspect-[2/3] overflow-hidden` with the image at `h-full w-full`,
   * so a bar in normal flow is pushed below a full-height image and clipped away
   * entirely — the card renders with no bars at all, which is exactly what
   * happened on the first build.
   */
  edge: "top" | "bottom";
}) {
  const pct = Math.min(100, Math.max(0, Math.round(fill)));
    /*
    The label rides `--library-badge-size`, the shelf's own density-aware token,
    rather than a fixed size. `validate-ui-typography` rejects a raw pixel value
    and it is right to: a card that ignores density is a card that stops matching
    the shelf around it at three of the four settings.
  */
  const layer = "pointer-events-none absolute inset-0 flex items-center justify-center whitespace-nowrap px-1 font-mono text-[length:var(--library-badge-size)] font-bold leading-none";
  /*
    **Baseline, not centre, between the lead and the number.**

    `items-center` centres the two BOXES, and the boxes are different sizes —
    the lead is 0.72em. Worse, the lead is uppercase with no descenders, so its
    glyphs sit in the top of its box while "0 / 2" fills its own; equal centres
    therefore read as the word floating above the number. James: *"center the
    subs top to bottom on the posters, it looks a bit odd and too high compared
    to the number"*.

    Two sizes of type on one line share a baseline. That is what the eye reads
    as level, and it is what the outer layer then centres as a single block.
  */
  const inner = label ? (
    <span className="flex items-baseline gap-1">
      {lead ? <i className="not-italic text-[0.72em] tracking-wider opacity-75">{lead}</i> : null}
      <b className="font-bold">{label}</b>
    </span>
  ) : null;

  return (
    <div
      role="img"
      aria-label={ariaLabel}
      title={title}
      className={cn(
        "absolute inset-x-0 z-10 overflow-hidden",
        /*
          **The bar shrinks when its words are switched off.**

          A 16px band exists to carry a label; with no label it is 16px of
          chrome over the artwork saying what 5px says. James: *"when these 2 are
          triggered off the bar should go smaller"*, and DESIGN-006 §6 already
          said it — *"with a switch off its bar falls back to the 5px strip
          Deluno ships today"*. The state and the fraction survive either way,
          which is the rule the switch must never break: it removes words, never
          facts.
        */
        inner ? "h-4" : "h-[5px]",
        edge === "top" ? "top-0" : "bottom-0"
      )}
      style={{ background: trackColour }}
    >
      <span
        aria-hidden="true"
        className={cn("absolute inset-y-0 left-0 block", pattern)}
        style={{ width: `${pct}%`, background: fillColour }}
      />
      {inner ? (
        <>
          {/*
            Both layers are aria-hidden: one string rendered twice must not be
            heard twice. What a screen reader gets is `ariaLabel`, a sentence.
          */}
          <span aria-hidden="true" className={layer} style={{ color: onTrack, clipPath: `inset(0 0 0 ${pct}%)` }}>
            {inner}
          </span>
          <span aria-hidden="true" className={layer} style={{ color: onFill, clipPath: `inset(0 ${100 - pct}% 0 0)` }}>
            {inner}
          </span>
        </>
      ) : null}
      {/* Keep the decorative texture after the two text layers. Apart from
          preserving the paint order, this means consumers that inspect the
          aria-hidden layers still see the fill followed by the two clipped
          label copies, rather than mistaking the texture for a label layer. */}
      {pattern ? <span aria-hidden="true" className={cn("title-mark-pattern", pattern)} /> : null}
    </div>
  );
}

const paintVar = (token: string) => `hsl(var(${token}))`;

export interface TitleBarsInput {
  type: MediaType;
  monitored?: boolean;
  wantedStatus?: string | null;
  isTransferring?: boolean;
  hasFile?: boolean;
  quality?: string | null;
  airedEpisodeCount?: number;
  airedWithFileCount?: number;
  subtitleLanguagesWanted?: number;
  subtitleLanguagesHeld?: number;
  subtitleLanguagesSettled?: number;
}

/**
 * What the top bar says and how far it fills, per medium.
 *
 * `fraction` states outright whether there IS a held part to colour. Inferring
 * it from the percentage does not work: a fully-held Continuing show is 100% and
 * does have a fraction, so testing `pct > 0 && pct < 100` silently excludes it.
 */
function mediaBarOf(item: TitleBarsInput, mark: TitleMark) {
  const design = cardDesign(item.type);
  const word = TITLE_MARK_PRESENTATION[mark].label;

  if (design.mediaBar === "episodes") {
    const aired = item.airedEpisodeCount ?? 0;
    // Nothing aired is not a fraction — an Upcoming show has not started.
    if (aired <= 0) return { pct: 100, label: word, lead: "EPS", fraction: false };
    const held = Math.min(Math.max(0, item.airedWithFileCount ?? 0), aired);
    return { pct: Math.round((held / aired) * 100), label: `${held} / ${aired}`, lead: "EPS", fraction: true };
  }

  const quality = (item.quality ?? "").trim();
  if (item.hasFile) return { pct: 100, label: quality || "On disk", lead: "QLTY", fraction: true };
  // Bytes moving is not a composition either — there is no held part yet.
  if (mark === "downloading") return { pct: 45, label: word, lead: "QLTY", fraction: false };
  // An Upcoming film is not 0% of anything; it has not been released.
  if (mark === "upcoming") return { pct: 100, label: word, lead: "QLTY", fraction: false };
  return { pct: 0, label: word, lead: "QLTY", fraction: true };
}

/**
 * Chooses the colour of the held portion of a bar.
 *
 * The composition is deliberately shared by the poster and the list: a held
 * episode is green, a fully held title at the requested quality is gold, and
 * the red Missing track remains visible behind any coverage that is not held.
 * A zero-width fill is still assigned the same colour; the track is what the
 * reader sees in that case.
 */
function barFillMark(
  design: ReturnType<typeof cardDesign>,
  mark: TitleMark,
  fillPercent: number,
  hasFraction: boolean
): TitleMark {
  if (design.fill === "held") {
    return fillPercent === 100 && mark === "covered" ? "covered" : "upgrade";
  }

  if (design.fill === "mixed" && mark === "missing" && hasFraction) {
    return "upgrade";
  }

  return mark;
}

/**
 * The two bars that book-end a poster: the media above, the subtitles below.
 *
 * <p><b>Nothing else sits on the artwork.</b> No corner pill, no title, no
 * monitoring mark. The title is `showTitle`, a switchable line beneath the
 * image, and monitoring is said by the bars themselves.</p>
 *
 * <p><b>Unmonitored overrides every colour rule</b>, on both bars, fill and
 * track alike — see {@link UNMONITORED_PAINT}.</p>
 */
export function TitleBars({
  item,
  showMediaText,
  showSubtitleText
}: {
  item: TitleBarsInput;
  showMediaText: boolean;
  showSubtitleText: boolean;
}) {
  const design = cardDesign(item.type);
  const mark = titleMark(item);
  const monitored = item.monitored !== false;
  const isShow = design.mediaBar === "episodes";

  const media = mediaBarOf(item, mark);
  const subs = titleBar(item);
  const files = isShow
    ? Math.max(0, item.airedWithFileCount ?? 0)
    : item.hasFile === false ? 0 : 1;

  /*
    The subtitle bar inherits **Upcoming, and nothing else**.

    Upcoming means the thing cannot exist yet, so nothing can be fetched and
    calling it Missing is a category error. Downloading means it exists and is
    arriving — its subtitles exist out there and you do not have them, which is
    precisely what Missing means. A subtitle is also a few kilobytes: a progress
    state for one would be gone before it could be read, and a state nobody can
    ever see should not be modelled.
  */
  const subState: TitleMark = files === 0 && mark === "upcoming" ? "upcoming" : "missing";
  const subSettled = subs.wanted > 0 && subs.held === subs.wanted;
  const subPct = files === 0
    ? (subState === "missing" ? 0 : 100)
    : subs.wanted ? Math.round((subs.held / subs.wanted) * 100) : 0;
  const subLabel = subs.wanted
    ? `${subs.held} / ${subs.wanted}`
    : TITLE_MARK_PRESENTATION[subState].label;

  const off = !monitored;
  const paint = (m: TitleMark) => (off ? UNMONITORED_PAINT : TITLE_MARK_PAINT[m]);

  const topMark = barFillMark(design, mark, media.pct, media.fraction);
  const topPaint = paint(media.fraction ? topMark : mark);
  /*
    The track is part of the medium's card grammar. Movies use Missing red as
    the remainder, so the track label uses the Missing surface's white label.
    TV uses the same Missing red remainder: coverage is the green held portion,
    Quality met is gold when the aired set is complete, and red is what remains
    to acquire. The subtitle bar can have a different state from the media bar
    (for example Upcoming with no files), hence two values.

    Unmonitored remains a complete override: both the fill and remainder are
    the same grey, with its surface label, regardless of the shelf grammar.
  */
  const trackFor = (barMark: TitleMark, fillPct: number) => {
    if (off) {
      return { surface: UNMONITORED_PAINT.surface, label: UNMONITORED_PAINT.onSurface };
    }
    if (design.track === "neutral") {
      if (fillPct <= 0) {
        return { surface: TITLE_MARK_PAINT[barMark].surface, label: TITLE_MARK_PAINT[barMark].onSurface };
      }
      return { surface: "--mark-idle", label: TITLE_MARK_PAINT[barMark].onTrack };
    }
    return { surface: TITLE_MARK_PAINT.missing.surface, label: TITLE_MARK_PAINT.missing.onSurface };
  };
  const topTrack = trackFor(mark, media.pct);
  const subPaint = paint(files === 0 ? subState : subSettled ? "covered" : "upgrade");
  const subTrack = trackFor(subState, subPct);

  const rung = TITLE_MARK_PRESENTATION[mark];
  const watch = monitored ? "" : " · unmonitored";

  return (
    <>
      <TwoToneBar
        fill={media.pct}
        fillColour={paintVar(topPaint.surface)}
        onFill={paintVar(topPaint.onSurface)}
        trackColour={paintVar(topTrack.surface)}
        onTrack={paintVar(topTrack.label)}
        lead={design.leads === "both" ? media.lead : undefined}
        label={showMediaText ? media.label : undefined}
        title={rung.hint + (monitored ? "" : " Deluno is not watching this one.")}
        edge="top"
        ariaLabel={`${rung.label}${watch}${
          isShow && media.fraction ? ` · ${media.label} aired episodes on disk` : ""
        }${!isShow && item.hasFile && item.quality ? ` · ${item.quality}` : ""}`}
        pattern={monitored ? rung.stateClass : undefined}
      />
      <TwoToneBar
        fill={subPct}
        fillColour={paintVar(subPaint.surface)}
        onFill={paintVar(subPaint.onSurface)}
        trackColour={paintVar(subTrack.surface)}
        onTrack={paintVar(subTrack.label)}
        lead={design.leads === "none" ? undefined : "SUBS"}
        label={showSubtitleText ? subLabel : undefined}
        edge="bottom"
        title={
          subs.wanted
            ? `${subs.held} of ${subs.wanted} subtitle languages you asked for are here.`
            : TITLE_MARK_PRESENTATION[subState].hint
        }
        ariaLabel={
          subs.wanted
            ? `${subs.held} of ${subs.wanted} subtitle languages${watch}`
            : `Subtitles ${TITLE_MARK_PRESENTATION[subState].label.toLowerCase()}${watch}`
        }
      />
    </>
  );
}

export function TitleMarkTopBar({
  item,
  className
}: {
  item: TitleMarkInput;
  className?: string;
}) {
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;
  const tracksEpisodes = typeof item.airedEpisodeCount === "number" && item.airedEpisodeCount > 0;
  const fillPercent = tracksEpisodes ? Math.round(titleProgress(item) * 100) : 100;

  const description = half
    ? `${presentation.label} · unmonitored`
    : presentation.label;

  return (
    <div
      role="img"
      aria-label={tracksEpisodes
        ? `${description} · ${episodesHeld(item)} of ${item.airedEpisodeCount} aired episodes on disk`
        : description}
      title={half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint}
      className={cn("absolute inset-x-0 top-0 z-10 h-[5px] overflow-hidden bg-mark-idle", className)}
    >
      {/*
        The fill is its own layer because `.mark-grail` sets `position:
        relative` and beats a Tailwind `absolute` on the same element — it did
        exactly that twice while the quality pill was being built, once dropping
        it out of the poster and once collapsing it to nothing, both times
        rendering the right gradient invisibly.
      */}
      <span
        aria-hidden="true"
        className={cn(
          "block h-full",
          presentation.stateClass,
          half
            ? "bg-[linear-gradient(90deg,currentColor_0_50%,hsl(var(--mark-idle))_50%_100%)]"
            : presentation.dot,
          half && presentation.text,
          !half && presentation.sheen
        )}
        style={{ width: `${fillPercent}%` }}
      />
      {item.monitored ? <span aria-hidden="true" className={cn("title-mark-pattern", presentation.stateClass)} /> : null}
    </div>
  );
}

/** Of the aired episodes, how many are on disk — never more than have aired. */
function episodesHeld(item: TitleMarkInput): number {
  return Math.min(Math.max(0, item.airedWithFileCount ?? 0), item.airedEpisodeCount ?? 0);
}

/**
 * The corner, where the dot used to be: the count for a show, the state's word
 * for a film.
 *
 * <p>This is the other half of the treatment James picked. The bar answers
 * <i>what state</i> across the whole top edge, where it can be read down a wall
 * of thirty posters; this answers <i>how far</i>, which is a number and has to
 * be read one card at a time. Two facts, two places, neither borrowing the
 * other's space.</p>
 *
 * <p><b>Opaque.</b> No translucent black, no backdrop blur: over artwork those
 * make the pill a different colour on every poster, which is the same class of
 * problem as the label that kept washing out. A solid ground and a full-colour
 * pip read identically on a white poster and a black one.</p>
 *
 * <p>A film says its state here rather than a fraction. It is here or it is
 * not, and "1 / 1" would be a fraction invented to fill a shape.</p>
 */
export function TitleMarkCorner({ item, className }: { item: TitleMarkInput; className?: string }) {
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;
  const tracksEpisodes = typeof item.airedEpisodeCount === "number" && item.airedEpisodeCount > 0;

  const text = tracksEpisodes
    ? `${episodesHeld(item)} / ${item.airedEpisodeCount}`
    : presentation.label;
  const description = half ? `${presentation.label} · unmonitored` : presentation.label;

  return (
    <span
      role="img"
      aria-label={tracksEpisodes ? `${description} · ${text} aired episodes on disk` : description}
      title={half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint}
      className={cn(
        "absolute right-2 top-2 z-20 inline-flex items-center gap-1.5 rounded-full",
        "border border-white/15 bg-surface-1 px-2 py-0.5 shadow-card",
        "text-[length:var(--library-badge-size)] font-bold tabular-nums text-foreground",
        className
      )}
    >
      <span
        aria-hidden
        className={cn(
          "relative flex h-2 w-2 shrink-0 items-center justify-center rounded-full",
          presentation.stateClass,
          half
            ? "bg-[linear-gradient(90deg,currentColor_0_50%,hsl(var(--mark-idle))_50%_100%)]"
            : presentation.dot,
          half && presentation.text,
          !half && presentation.sheen
        )}
      >
        <span aria-hidden data-glyph={presentation.glyph} className="title-mark-glyph pointer-events-none text-[0.58em] font-black leading-none text-white" />
      </span>
      {text}
    </span>
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
 * <p>The colour follows the same composition as the poster: held coverage is
 * green, a fully held Quality met show is gold, and a monitored remainder is
 * Missing red. Gold takes dark text for the same reason the state bar does:
 * every rung on the ladder is a light colour and white would vanish into it.</p>
 *
 * <p>A film has no fraction — it is here or it is not — so this draws nothing
 * and the caller shows a dash. That is not the same as zero of zero, which is
 * what a show whose episodes Deluno has not learned about yet has.</p>
 */
export function EpisodeProgressBar({ item, type, className }: { item: TitleMarkInput; type?: MediaType; className?: string }) {
  const aired = item.airedEpisodeCount;
  if (typeof aired !== "number" || aired <= 0) {
    return null;
  }

  const held = Math.min(Math.max(0, item.airedWithFileCount ?? 0), aired);
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const design = type ? cardDesign(type) : null;
  const percent = Math.round((held / aired) * 100);
  const fillMark = design && item.monitored !== false
    ? barFillMark(design, mark, percent, true)
    : mark;
  const paint = design?.bars
    ? item.monitored === false ? UNMONITORED_PAINT : TITLE_MARK_PAINT[fillMark]
    : null;
  const track = paint
    ? item.monitored === false
      ? paint
      : design?.track === "missing"
        ? TITLE_MARK_PAINT.missing
        : percent <= 0
          ? paint
          : { surface: "--mark-idle" }
    : null;
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
      style={track ? { backgroundColor: paintVar(track.surface) } : undefined}
    >
      {/*
        The fill is its own layer rather than a background on the box, because
        `.mark-grail` sets `position: relative` and beats a Tailwind `absolute`
        on the same element — it has dropped an element out of its parent twice
        in this file already.
      */}
      <span aria-hidden className="absolute inset-y-0 left-0" style={{ width: `${percent}%` }}>
        <span
          className={cn("block h-full w-full", !paint && presentation.dot, paint && item.monitored !== false && presentation.sheen)}
          style={paint ? { backgroundColor: paintVar(paint.surface) } : undefined}
        />
      </span>
      <span className={cn("relative px-1.5", labelTone(percent))}>
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
 *
 * <b>Monitoring is not part of this legend.</b> Unmonitored is a grey override
 * rather than another lifecycle rung. The shelf control rail places its
 * filter immediately after Upcoming, behind a divider, so this component can
 * stay a subtitle-only legend.
 */
export function TitleMarkBarLegend({ className, type }: { className?: string; type?: MediaType }) {
  return (
    <div className={cn("flex shrink-0 items-center gap-2.5 whitespace-nowrap", className)}>
      <span
        role="heading"
        aria-level={3}
        className="shrink-0 text-[length:var(--library-toolbar-size)] font-semibold text-foreground"
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
          <MarkStrip mark={segment.mark} type={type} sheen />
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
 * screen reader gets "Missing · unmonitored" once, in the order a sighted
 * reader gets it, instead of a decorative dot followed by two unrelated
 * fragments. The dot inside is hidden for the same reason.
 */
export function TitleMarkLabel({ item, className, type }: { item: TitleMarkInput; className?: string; type?: MediaType }) {
  const mark = titleMark(item);
  const presentation = TITLE_MARK_PRESENTATION[mark];
  const half = !item.monitored && presentation.canBeHalf;
  const label = half ? `${presentation.label} · unmonitored` : presentation.label;

  return (
    <span
      role="img"
      aria-label={label}
      title={half ? `${presentation.hint} Deluno is not watching this one.` : presentation.hint}
      className={cn("inline-flex items-center gap-1.5 whitespace-nowrap", className)}
    >
      {/*
        A strip, not a dot.

        The shelf stopped drawing dots when the state became a bar across the
        poster's top — the legend row moved to `MarkStrip` for exactly that
        reason, and this label did not follow. James, circling it on the detail
        page: *"we should not be using dots anymore"*. A page that teaches a
        shape the rest of the product no longer draws is teaching the wrong
        thing.

        `type` is passed so it paints from the bar SURFACE on a shelf that has
        adopted DESIGN-006 — the same colour the card carries, rather than the
        page-text one. `monitored` for the same reason one step further: grey is
        the override, and a row that ignored it called a title Missing red while
        its own poster called it grey.
      */}
      <MarkStrip mark={mark} type={type} monitored={item.monitored} sheen />
      <span>{presentation.label}</span>
      {half ? <span className="text-muted-foreground">· unmonitored</span> : null}
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

  const label = half ? `${presentation.label} · unmonitored` : presentation.label;

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
