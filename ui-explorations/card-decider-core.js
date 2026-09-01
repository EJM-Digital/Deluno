/*
  The card, drawn once, for whichever shelf asks for it.

  There are two decider pages — Movies and TV — because James found one page
  carrying both too hard to read: *"we need to split this up I think, its too
  confusing for me and everyone... lets focus on Movies first"*. He is right, and
  splitting it immediately proved the point: **Continuing does not exist on the
  movie shelf at all**, so the whole magenta question was noise on half the page.

  Two pages, one file. The pages differ in the prose they carry and the medium
  they mount with; every rule about how a card is drawn lives here exactly once,
  because one rule written twice in two places that cannot check each other is the
  shape of every defect worth finding in this codebase.

  Mount with:  mountDecider({ medium: "movies" })  or  { medium: "tv" }
*/

/* The render is useful in two places: served by Deluno Lab, and opened directly
   from this folder while we review the spec. Keep both paths pointed at the same
   artwork/API origin without starting a second local application. */
const DEFAULT_RENDER_ORIGIN = "http://192.168.1.142:5099";
function renderOrigin() {
  try {
    const requested = new URLSearchParams(location.search).get("origin");
    if (requested) {
      const url = new URL(requested);
      if (url.protocol === "http:" || url.protocol === "https:") return url.origin;
    }
    if (location.protocol === "http:" || location.protocol === "https:") return location.origin;
  } catch (e) { /* use the lab origin below */ }
  return DEFAULT_RENDER_ORIGIN;
}
const RENDER_ORIGIN = renderOrigin();
function renderUrl(value) {
  if (!value) return "";
  try { return new URL(value, RENDER_ORIGIN).href; } catch (e) { return value; }
}

/* ══════════════════════════════════════════════════════════════
   Colour maths — so every number on the page is computed, never typed
   ══════════════════════════════════════════════════════════════ */
function hsl2rgb(h, s, l) {
  s /= 100; l /= 100;
  const k = n => (n + h / 30) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = n => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return [f(0), f(8), f(4)].map(v => v * 255);
}
function toLab(r, g, b) {
  const f = c => { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  [r, g, b] = [f(r), f(g), f(b)];
  let X = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
  let Y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  let Z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
  const t = c => c > 0.008856 ? Math.cbrt(c) : 7.787 * c + 16 / 116;
  [X, Y, Z] = [t(X), t(Y), t(Z)];
  return [116 * Y - 16, 500 * (X - Y), 200 * (Y - Z)];
}
const parseHsl = str => hsl2rgb(...str.split(/[ %]+/).map(parseFloat));
function deltaE(a, b) {
  const A = toLab(...parseHsl(a)), B = toLab(...parseHsl(b));
  return Math.hypot(A[0] - B[0], A[1] - B[1], A[2] - B[2]);
}
function relLum(str) {
  const f = c => { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  const [r, g, b] = parseHsl(str).map(f);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
const whiteOn = str => 1.05 / (relLum(str) + 0.05);

/* ══════════════════════════════════════════════════════════════
   The ladder
   ══════════════════════════════════════════════════════════════ */
const MARKS = {
  miss: "Missing", down: "Downloading", upg: "Upgradable",
  cont: "Continuing", gold: "Quality met", soon: "Upcoming"
};

/* Continuing is a *show* state — a film is never "still airing". The movie
   shelf never draws it, never legends it, and never asks about its colour. */
const LADDER_FOR = {
  movies: ["miss", "down", "upg", "gold", "soon"],
  tv:     ["miss", "down", "upg", "cont", "gold", "soon"]
};

const SURFACES = {
  shipped: {
    dark:  { miss:"0 84% 62%",  down:"207 96% 62%", upg:"145 78% 52%", cont:"184 78% 52%", gold:"49 100% 62%", soon:"265 82% 72%" },
    light: { miss:"0 84% 48%",  down:"207 92% 45%", upg:"145 72% 34%", cont:"184 72% 34%", gold:"49 100% 62%", soon:"265 62% 52%" }
  },
  /* Deep and Jewel are ONE set for both themes: a bar sits on artwork, not on
     the page, so it has no reason to invert. Same argument as --mark-leaf-*. */
  deep:  { both: { miss:"356 84% 41%", down:"214 94% 40%", upg:"150 90% 25%", cont:"318 78% 38%", gold:"49 100% 62%", soon:"270 76% 47%" } },
  jewel: { both: { miss:"352 88% 34%", down:"219 96% 33%", upg:"156 94% 20%", cont:"182 100% 19%", gold:"49 100% 62%", soon:"272 84% 39%" } }
};

/*
  A THIRD set: the label where it sits on the empty track.

  Not the surface (tuned for white-on-bar) and not the card text (tuned for the
  card's background). The track is `--mark-idle` — lighter than the dark card and
  darker than the light one — so a colour that reads on the card can fail on it.
  Mine did, on 40 card/switch combinations: Missing red on the dark track came out
  at 2.91:1, and the unmonitored grey on the light track at 1.79:1.

  Solved rather than guessed: for each rung, the lightness that first clears 4.6:1
  against that theme's track, searching upward on the dark track and downward on
  the light one. The audit re-checks every value against the element it lands on.
*/
const ON_TRACK = {
  dark:  { miss:"0 84% 77%", down:"207 92% 67%", upg:"145 72% 45%", cont:"318 78% 75%",
           gold:"44 98% 45%", soon:"268 72% 78%", off:"220 8% 69%" },
  light: { miss:"0 84% 37%", down:"207 92% 32%", upg:"145 72% 23%", cont:"318 78% 35%",
           gold:"44 98% 22%", soon:"268 72% 45%", off:"220 8% 35%" }
};

/* The text colour for a count or a word on a ground — per theme, and NOT the
   surface. The deep surfaces used as text fail AA on the dark card. */
const TEXT = {
  dark:  { miss:"0 84% 62%", down:"207 96% 62%", upg:"145 78% 52%", cont:"318 76% 50%", gold:"44 98% 58%", soon:"268 82% 72%" },
  light: { miss:"0 84% 48%", down:"207 92% 45%", upg:"145 72% 34%", cont:"318 74% 30%", gold:"42 96% 40%", soon:"268 62% 50%" }
};

/* Gold is floored at 52% lightness, so white can never sit on it. Every other
   bar takes white; gold takes near-black. Forced, not a preference. */
const labelOn = mark => mark === "gold" ? "hsl(40 90% 12%)" : "hsl(0 0% 100%)";

/*
  How a card says "Deluno is not watching this one".

  The road here is worth keeping, because three of the four attempts were mine and
  each was wrong for a different reason.

  1. DESIGN-001 gives an unmonitored title a half-grey **dot**. This render halved
     the **bar's fill** instead. James: *"I think the half was in reference to the
     dots which we have removed"* — right, and the measurement agreed before the
     argument did: on a Missing title, whose fill is 0% wide, the half rendered as
     nothing at all. A half works on a dot because a dot has no length of its own;
     a bar IS a length, and that length already means the fraction you hold.

  2. Overriding the bars with a flat neutral. Worked, and I talked myself out of it
     on the grounds that it "spends the bar's colour on a fact that is not about the
     title's state" — which is backwards: an unmonitored title's rung is not telling
     you to do anything, so its colour is the thing least worth keeping.

  3. A shield badge in the corner of the artwork. Reads well, but it is another
     thing on the picture, and Deluno already says monitoring in words on a line
     under the poster behind its own switch.

  4. What James settled on: *"the poster option should stay as a grey or black bar
     on the poster and the shield removed from the poster and kept under in the
     selectable options"*. **The bars go flat grey, the artwork stays clean, and
     the shield keeps its existing home under the poster.** Two facts, two places,
     neither borrowing the other's space — which is the same rule the top and
     bottom bars already follow.

  Black or grey is a switch. The thing to watch is that the TRACK is already grey,
  so a grey fill on a grey track could make the bar vanish and take the fraction
  with it — measured, both stay clear of it (black ΔE 19.2 dark / 74.9 light; grey
  21.3 / 35.5), so this is a taste call rather than a legibility one.
*/
/*
  **Unmonitored is an override, and it is always grey.**

  James, settling it: *"unmonitored titles are the override, they are always grey —
  once they are monitored they inherit the normal statuses"*.

  So there is one rule and no switch. If Deluno is not watching a title, both its
  bars are grey, whatever rung the title happens to sit on — because that rung is
  not telling you to do anything. The moment it is monitored again it inherits the
  ladder exactly as any other title does; nothing is remembered and nothing is
  special-cased.

  This is the only override in the design. Everywhere else, colour is decided by the
  title's state; here the state is overruled outright.

  The grey is a mid grey, not the track's. The track is `--mark-idle` — 26%
  lightness in dark, 82% in light — and a fill the same value as its track would
  make the bar vanish and take the fraction with it. Measured: ΔE 21.3 against the
  dark track, 35.5 against the light one, and white sits on it at 4.82.
*/
/*
  **One grey, flat, both bars.**

  James: *"why is the martian different grey to mad max and big buck bunny come
  on."* Because the override was applied to the fill and the track as two separate
  greys: The Martian is a Missing film, so its fill is 0% wide and what you see is
  the TRACK; Mad Max and Big Buck Bunny have 100% fills, so what you see is the
  FILL. Two greys, and which one you got depended on the title's rung — the very
  thing an override is supposed to stop mattering.

  So the fill and the track are the SAME value. An unmonitored title is one flat
  grey bar, identical on every card, whatever its rung and whatever its fraction.
  The count or the quality is still written on it, so the number survives; only the
  colour goes, which is the point.
*/
const UNMONITORED = {
  fill:  "hsl(220 8% 46%)",
  track: "hsl(220 8% 46%)",
  label: "hsl(0 0% 100%)"
};

/*
  There is no monitoring line, and `showMonitored` should stop being an option.

  It was kept underneath at first — I read *"kept under in the selectable options"*
  as *keep the line under the poster*. James: *"why is monitored under the poster,
  it doesnt need to be there anymore"*. He is right, and the reason is the one this
  codebase has already acted on once: **when a bar starts saying a fact, the switch
  that used to say it is removed.** `4bdfe45` deleted the Quality poster option the
  moment the bar carried the quality; `showEpisodeProgress` went the same way. A
  line reading "Not monitored" beneath a card whose bars have already gone neutral
  is the same fact twice.

  So monitoring is said by the bars alone, it is not switchable, and
  `CatalogueControls.cs` loses `showMonitored` exactly as it lost the other two.
*/

/* ── Continuing's hue: a TV-only question ─────────────────────── */
const CONT_CANDIDATES = {
  magenta: { label: "Magenta 318 — recommended", hsl: "318 78% 38%" },
  steel:   { label: "Steel",                    hsl: "200 26% 40%" },
  pink:    { label: "Pink 330",                 hsl: "330 78% 40%" },
  teal:    { label: "Teal 178",                 hsl: "178 96% 24%" },
  cyan:    { label: "Cyan 192",                 hsl: "192 95% 27%" },
  lime:    { label: "Lime 92",                  hsl: "92 88% 24%" }
};
const NEIGHBOURS = {
  Missing: "356 84% 41%", Downloading: "214 94% 40%", Upgradable: "150 90% 25%",
  "Quality met": "49 100% 62%", Upcoming: "270 76% 47%", "Needs you (reserved)": "28 96% 48%"
};
function nearestTo(hsl) {
  let best = { name: "", dE: Infinity };
  for (const name of Object.keys(NEIGHBOURS)) {
    const dE = deltaE(hsl, NEIGHBOURS[name]);
    if (dE < best.dE) best = { name, dE };
  }
  return best;
}

/* ══════════════════════════════════════════════════════════════
   State
   ══════════════════════════════════════════════════════════════ */
const S = {
  theme: "dark",
  depth: "deep",
  /* The two USER switches. These are not design decisions — they are the
     poster options a person turns on and off in the View drawer, and the card
     has to survive every combination of them. James: "some of these options are
     selectable for on and off so what are we doing about that here". */
  media: "on",         // "Quality on the bar" / "Episode count on the bar"
  subs: "on",          // "Subtitle count on the bar"
  leads: "subs",       // none | subs | both — the DESIGN choice, lead words
  rem: "missing",      // Recommended: the part not held is Missing red
  fill: "held",        // Recommended: held green; Quality met gold when complete
  cont: "magenta",     // Continuing remains a TV-only state in the legend.
  size: "md"
};

const CONT_OPTS = Object.keys(CONT_CANDIDATES).map(k => [k, CONT_CANDIDATES[k].label]);

function controlsFor(medium) {
  const base = [
    { key: "theme",  label: "Theme",  opts: [["dark","Dark"],["light","Light"]] },
    { key: "depth",  label: "Depth",  opts: [["shipped","Shipped"],["deep","Deep"],["jewel","Jewel"]] },
    { key: "media",  label: medium === "tv" ? "Episode count" : "Quality on bar",
      opts: [["on","On"],["off","Off"]], user: true },
    { key: "subs",   label: "Subtitle count", opts: [["on","On"],["off","Off"]], user: true },
    { key: "leads",  label: "Lead words", opts: [["none","None"],["subs","SUBS only"],["both","Both"]] },
    /* The recommended track is the composition rule: the part not held is
       Missing red. Neutral is retained only for comparing Sonarr's grammar. */
    { key: "rem",    label: "Track",  opts: [["missing","Missing red — recommended"],["neutral","Neutral remainder — exploratory"]] },
  ];
  /*
    **Two controls the movie shelf must not carry**, and both for the same reason:
    a film is one file.

    `cont` — a film is never still airing, so Continuing does not exist here.

    `fill` — the fill rule decides how to colour the part you HOLD, and a film has
    no partial coverage: it is held (100%) or it is not (0%), and download progress
    is explicitly non-compositional because there is no held part yet. Measured
    rather than reasoned: across all three fill rules, **zero** movie cards render
    differently, against **13** of the TV cards. James: *"this one isnt changing
    anything at all"* — he was looking at the movie page, and it genuinely was not.

    A switch that cannot change what you are looking at is worse than no switch: it
    invites you to keep flipping it looking for the difference.
  */
  if (medium === "tv") {
    base.push({ key: "cont", label: "Continuing", opts: CONT_OPTS });
    base.push({ key: "fill", label: "Fill", opts: [["held","Held green · Quality met gold — recommended"],["mixed","State, held green"],["state","State colour — exploratory"]] });
  }
  base.push({ key: "size", label: "Card", opts: [["sm","Small"],["md","Medium"],["lg","Large"]] });
  return base;
}

const PRESETS = [
  { label: "Recommended — Deluno composition", why: "held green, Quality met gold, Missing red remainder, and grey only for unmonitored",
    set: { fill: "held", rem: "missing" } },
  { label: "Alternative — Sonarr grammar", why: "state-coloured fill over a neutral remainder, for comparison",
    set: { fill: "state", rem: "neutral" } }
];

function surfaces() {
  const set = SURFACES[S.depth];
  const base = set.both || set[S.theme];
  return Object.assign({}, base, { cont: CONT_CANDIDATES[S.cont].hsl });
}

/* ══════════════════════════════════════════════════════════════
   The rules, mirrored from lib/status-tones.ts
   ══════════════════════════════════════════════════════════════ */
const MARK_OF = { covered:"gold", upgrade:"upg", upcoming:"soon", airing:"cont", downloading:"down", missing:"miss" };
const markFor = it => MARK_OF[it.wantedStatus] || "miss";

function subtitleBar(it, isShow) {
  const perFile = Math.max(0, it.subtitleLanguagesWanted || 0);
  const held = Math.max(0, it.subtitleLanguagesHeld || 0);
  const files = isShow ? Math.max(0, it.airedWithFileCount || 0) : (it.hasFile === false ? 0 : 1);
  const wanted = perFile * files;
  return { held: Math.min(held, wanted), wanted, files };
}

/*
  What a subtitle bar says when there is nothing to count, and in whose colour.

  **It inherits Upcoming, and nothing else.**

  The first version said *not here is Missing* for every title, which threw away the
  distinction the ladder exists to make — James: *"upcoming is the wrong colour, its
  missing yes but its upcoming too"*. So it inherited the title's reason for holding
  no files, whatever that reason was. That over-corrected, and he caught that too:
  *"subs should not be downloading it should always be missing"*.

  He is right, and the reason is sharper than the one he offered. **Upcoming and
  Downloading are different in kind.** Upcoming means the thing *cannot exist yet* —
  not released, not aired — so nothing can be fetched and calling it Missing is a
  category error. Downloading means it exists and is arriving, so its subtitles
  exist out there and you do not have them, which is precisely what Missing means.

  His own argument stands as the second reason: a film's download is worth a
  progress bar because it lasts, while a subtitle is a few kilobytes of text and
  would be here and gone before the bar could be read. A state nobody can ever see
  should not be modelled — the same rule that keeps a filter chip which can never
  match off the shelf.

  Unmonitored still overrides everything, as it does everywhere.
*/
function subtitleState(subs, mark) {
  if (subs.files === 0 && mark === "soon") return "soon";
  return "miss";
}

/*
  A show's bar is coverage of what has aired. A film's is download progress — it
  is not partway through itself, so its fill is free to mean the one fraction a
  film actually has.

  `fraction` says outright whether there IS a held part to colour. Inferring it
  from the percentage does not work: a fully-held Continuing show is 100% and has
  a fraction, and testing `pct > 0 && pct < 100` silently excluded it.
*/
function mediaBar(it, isShow) {
  const mark = markFor(it);
  if (isShow) {
    const aired = it.airedEpisodeCount || 0;
    const held = Math.min(Math.max(0, it.airedWithFileCount || 0), aired);
    if (aired <= 0) return { pct: 100, label: MARKS[mark], lead: "EPS", fraction: false };
    return { pct: Math.round(held / aired * 100), label: held + " / " + aired, lead: "EPS", fraction: true };
  }
  const quality = (it.currentQuality || "").trim();
  if (it.hasFile && quality) return { pct: 100, label: quality, lead: "QLTY", fraction: true };
  if (it.hasFile) return { pct: 100, label: "On disk", lead: "QLTY", fraction: true };
  if (mark === "down") return { pct: 45, label: MARKS[mark], lead: "QLTY", fraction: false };
  /* An Upcoming film is not 0% of anything — it has not been released. It draws
     solid, exactly as an Upcoming show with nothing aired does, so the two
     shelves agree. Only a Missing film is genuinely an empty bar. */
  if (mark === "soon") return { pct: 100, label: MARKS[mark], lead: "QLTY", fraction: false };
  return { pct: 0, label: MARKS[mark], lead: "QLTY", fraction: true };
}

/* ══════════════════════════════════════════════════════════════
   What each status means, in the app's own words

   Lifted from `TITLE_MARK_PRESENTATION` in lib/status-tones.ts, which is the one
   place a state gets a meaning. Two rungs need a different sentence per shelf,
   because the thing they describe is different: for a film "it" is a file, for a
   show "it" is a collection of episodes. The rest read the same either way.
   ══════════════════════════════════════════════════════════════ */
const HINTS = {
  miss: {
    movies: "It is out and Deluno does not have it yet. Deluno searches on the library's schedule.",
    tv: "At least one episode that has aired is not on disk. Deluno searches on the library's schedule."
  },
  down: { both: "Coming down, processing, or importing right now." },
  upg: {
    movies: "Here and watchable tonight. Deluno is still looking for a better copy.",
    tv: "Every aired episode is here and watchable. Deluno is still looking for better copies."
  },
  cont: { tv: "You have every episode that has aired. More are still to come, and Deluno will look for each as it does." },
  gold: { both: "This is the quality your Library Profile asked for, so Deluno has stopped looking." },
  soon: {
    movies: "Not released yet. Deluno will start looking on release.",
    tv: "Nothing has aired yet. Deluno will start looking as episodes air."
  }
};
const hintFor = (mark, medium) => (HINTS[mark] || {}).both || (HINTS[mark] || {})[medium] || "";

/*
  Why THIS card's bar is the length it is.

  The hint says what the status means; this says what the bar in front of you is
  doing, which is the part that is not obvious from a colour and a number.
*/
function barNote(media, subs, mark, isShow) {
  const top = !media.fraction
    ? (mark === "soon"
        ? (isShow ? "Solid — nothing has aired, so there is no fraction to draw."
                  : "Solid — a film that is not out yet is not partway through anything.")
        : "Filled to how far the download has got.")
    : isShow
      ? "Filled to " + media.label + " aired episodes on disk."
      : (media.pct === 100 ? "Solid — a film is one file, so it is here or it is not."
                           : "Empty — the file is not here.");
  const bottom = subs.files === 0
    ? (mark === "soon"
        ? "Nothing is out yet, so nothing can be fetched — the bar says Upcoming rather than claiming Missing."
        : "No files yet, so no subtitles yet — they exist out there and you do not have them, which is Missing.")
    : subs.wanted === 0
      ? "Subber has not resolved this title, so its languages are not here yet."
      : "Filled to the languages you asked for that are actually here.";
  return { top, bottom };
}

/* ══════════════════════════════════════════════════════════════
   Drawing
   ══════════════════════════════════════════════════════════════ */
const BARH = 16;
const esc = s => String(s).replace(/[<>&"]/g, c => ({ "<":"&lt;", ">":"&gt;", "&":"&amp;", '"':"&quot;" }[c]));

function twoTone(fillColour, pct, label, lead, onFill, remColour, onRem) {
  const inner = (lead ? '<i class="lead">' + lead + '</i>' : '') + '<b>' + esc(label) + '</b>';
  return '<div class="bar" style="height:' + BARH + 'px;background:' + remColour + '">'
    + '<div class="fill" style="width:' + pct + '%;background:' + fillColour + '"></div>'
    /*
      Two layers, same string, same place — and **each clipped to its own half**.

      The front is clipped to the fill; the back is clipped to the COMPLEMENT of
      the fill. That second clip was missing, and the bug it caused is the one
      James kept seeing: on a fully-filled bar both layers painted the identical
      white glyphs on top of each other, so every antialiased edge pixel was
      composited twice and the text thickened and glowed. *"the font on green deep
      still looks so overexposed"* — it was literally a double exposure, worst on
      saturated grounds where the halo shows.

      Clipped this way each glyph region is painted exactly once, and the two
      halves tile at the fill edge with no seam and no overlap.
    */
    + '<div class="txt" style="color:' + onRem + ';clip-path:inset(0 0 0 ' + pct + '%)">'
    + '<span>' + inner + '</span></div>'
    + '<div class="txt" style="color:' + onFill + ';clip-path:inset(0 ' + (100 - pct) + '% 0 0)">'
    + '<span>' + inner + '</span></div>'
    + '</div>';
}

/*
  The track.

  The approved composition is deliberately literal: the remainder is Missing
  red, the held portion is green, and a fully held Quality met title is gold.
  Unlike the Sonarr comparison, no monitored card uses grey. Unmonitored is the
  only override and is applied to both layers before this rule is considered.

  Neutral remains available as an exploratory renderer setting so the supplied
  Sonarr reference can still be compared, but it is not the product default.
*/
function track(mark, pct) {
  if (S.rem === "neutral") {
    if (pct <= 0) return { colour: "hsl(" + surfaces()[mark] + ")", label: labelOn(mark) };
    return { colour: "var(--idle)", label: "hsl(" + ON_TRACK[S.theme][mark] + ")" };
  }
  return { colour: "hsl(" + surfaces().miss + ")", label: "hsl(0 0% 100%)" };
}

/*
  With the track painted Missing red, a Missing title's fill must not ALSO be red or
  the bar goes flat and the fraction vanishes — Severance at 3 of 20 drawing the same
  bar as Foundation at 0 of 29. `held` makes the held portion green regardless of
  the lifecycle rung; Quality met is the one exception because it means the target
  quality has been reached.
*/
function fillColourFor(mark, fullyHeld, C) {
  if (S.fill === "held") return "hsl(" + (fullyHeld && mark === "gold" ? C.gold : C.upg) + ")";
  if (S.fill === "mixed" && mark === "miss") return "hsl(" + C.upg + ")";
  return "hsl(" + C[mark] + ")";
}

function cardHtml(it, isShow, withCaption) {
  const C = surfaces(), mark = markFor(it), monitored = it.monitored !== false;
  const media = mediaBar(it, isShow);
  const subs = subtitleBar(it, isShow);
  const subPct = subs.wanted ? Math.round(subs.held / subs.wanted * 100) : 0;
  /* "None asked for" is not a state: subtitles Subber has not resolved are
     simply not here, and not here is Missing. No denominator to print, so the
     bar prints the word — as a film's media bar does with no quality to name. */
  const subState = subtitleState(subs, mark);
  const subLabel = subs.wanted ? subs.held + " / " + subs.wanted : MARKS[subState];


  /* Unmonitored wins over every colour rule, on both bars. */
  const off = monitored ? null : UNMONITORED;
  /* Unmonitored overrules the Track choice as well as the Fill: it is an override
     on the whole bar, not a recolouring of part of it. */
  const NEUTRAL = { colour: UNMONITORED.track, label: UNMONITORED.label };
  const T = off ? NEUTRAL : track(mark, media.pct);
  const topFillFlat = media.fraction ? fillColourFor(mark, media.pct === 100, C) : "hsl(" + C[mark] + ")";
  const topFill = off ? off.fill : topFillFlat;
  const topOnFill = off ? off.label
    : topFillFlat === "hsl(" + C.gold + ")" ? labelOn("gold") : "hsl(0 0% 100%)";

  const subSettled = subs.wanted > 0 && subs.held === subs.wanted;
  /* With no files there is nothing held, so the bar carries Upcoming at full
     width rather than inventing a green fraction. */
  const subNoFiles = subs.files === 0;
  const subPctDrawn = subNoFiles ? (subState === "miss" ? 0 : 100) : subPct;
  const TS = off ? NEUTRAL : track(subState, subPctDrawn);
  const subFillFlat = subNoFiles ? "hsl(" + C[subState] + ")"
    : subSettled ? "hsl(" + C.gold + ")" : "hsl(" + C.upg + ")";
  const subFill = off ? off.fill : subFillFlat;
  const subOnFill = off ? off.label
    : subNoFiles ? labelOn(subState)
    : subSettled ? labelOn("gold") : labelOn("upg");

  /* Each bar answers to its own user switch. With the text off a bar falls back
     to the 5px strip Deluno ships today — it keeps saying the state and the
     fraction, it just stops spelling them out. Turning a switch off must never
     remove a fact that has nowhere else to live, and here it does not: the
     colour and the length both survive. */
  const mediaText = S.media === "on";
  const subsText = S.subs === "on";
  const mediaLead = S.leads === "both" ? media.lead : null;
  const subLead = S.leads === "none" ? null : "SUBS";

  const thin = (pct, colour, t) => '<div class="bar" style="height:5px;background:' + t.colour
    + '"><div class="fill" style="width:' + pct + '%;background:' + colour + '"></div></div>';

  const topBar = mediaText
    ? twoTone(topFill, media.pct, media.label, mediaLead, topOnFill, T.colour, T.label)
    : thin(media.pct, topFill, T);
  const botBar = subsText
    ? twoTone(subFill, subPctDrawn, subLabel, subLead, subOnFill, TS.colour, TS.label)
    : thin(subPctDrawn, subFill, TS);

  /*
    **Nothing but the two bars and the monitoring mark is on this card.**

    The title was first drawn over the artwork behind a gradient, which is not
    what the shelf does — it is `showTitle`, a switchable line UNDER the poster.
    Moving it there was still wrong for this exercise: James, *"still wrong take
    them out entirely please for this exercise its not needed as we mentioned its
    a switchable line"*. He is right. A switchable line that is not part of the
    decision adds height, competes for attention, and invites judging the card on
    something that is not being decided.

    The name is in the description underneath instead, where it identifies the
    card without being part of it.
  */
  const art = '<div class="art">'
    + (it.posterUrl ? '<img loading="lazy" data-artwork src="' + esc(renderUrl(it.posterUrl)) + '" alt="">'
                    : '<div class="noart">no art</div>')
    + '</div>';


  /* No corner pill, and the bars are always on the artwork — both settled:
     "corner pill is a complete removal and bars always on artwork". */
  /* The whole card: two bars and the artwork. Unmonitored is said by both bars
     becoming one flat grey, and by nothing else. */
  const card = '<div class="card">' + topBar + art + botBar + '</div>';
  if (!withCaption) return card;

  const note = barNote(media, subs, mark, isShow);
  const halfNote = off
    ? " Deluno is not watching this one, so both bars take the one grey override."
    : "";
  return '<div class="titled">' + card
    + '<div class="cap">'
    + (it.scenario ? '<i class="scen">' + esc(it.scenario) + '</i>' : '')
    + '<i class="who">' + esc(it.title) + '</i>'
    + '<b style="color:hsl(' + TEXT[S.theme][mark] + ')">' + MARKS[mark]
    + (it.monitored === false ? ' <span class="nm">not monitored</span>' : '') + '</b>'
    + '<p>' + esc(hintFor(mark, isShow ? "tv" : "movies") + halfNote) + '</p>'
    + '<p class="says"><span>Top</span> ' + esc(note.top) + '</p>'
    + '<p class="says"><span>Bottom</span> ' + esc(note.bottom) + '</p>'
    + '</div></div>';
}

/* ══════════════════════════════════════════════════════════════
   Every scenario, once.

   This was an arbitrary handful of titles and it showed Missing three times,
   Upgradable twice and Quality met twice while covering the unmonitored half not
   at all — James: *"there are some duplicates as well... be sure to cover ALL
   scenarios, toggles on and off etc etc"*.

   So it is a catalogue now, not a sample: **one card per distinct scenario, no
   repeats**, and it is drawn whether or not the page is signed in — a real
   library cannot be relied on to contain a downloading title or an unmonitored
   one at the moment you happen to look, and those are exactly the cards a design
   fails on. The real library is drawn underneath it, as itself.
   ══════════════════════════════════════════════════════════════ */
/*
  **Every combination, monitored and unmonitored — enumerated, not hand-picked.**

  James: "there should be every possible combination monitored and every possible
  combination unmonitored", after catching that a hand-listed catalogue had grown a
  duplicate: two cards drawing the identical shape (Quality met / monitored / 100% /
  100%), differing only in string length. My audit had asserted that scenario NAMES
  were unique, which is a different question, and is why it passed.

  So the catalogue is the cross-product of the state space:

      rung  x  subtitle state  x  monitored

  A title holding no files has no independent subtitle state — it inherits the
  title's own reason for having none — so those rungs contribute one row rather
  than four. Artwork repeats where there are more states than posters, because the
  picture is not the subject; the STATE never repeats, and the audit proves it by
  comparing rendered shapes rather than labels.
*/
const SCENARIOS = {
  movies: [
    { scenario: "Quality met - subtitles complete",
      title: "Arrival", posterUrl: "/api/metadata/artwork/473be3f38acc67c4b8289452deeabbadd4f65746808c84eae1c4d06fd29b691c",
      wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:2 },
    { scenario: "Quality met - subtitles short",
      title: "Big Buck Bunny", posterUrl: "/api/metadata/artwork/8caa0b5403699888fe15bc4b32e91244d74474ab5cb4bb3cd6e49a17235d7598",
      wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:1 },
    { scenario: "Quality met - no subtitles held",
      title: "Blade Runner 2049", posterUrl: "/api/metadata/artwork/8225563f8dad6e6bb1fea1e451eed27ac0c67543ed01aab55ef4a139f8d54e5e",
      wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Quality met - Subber has not resolved it",
      title: "Dune", posterUrl: "/api/metadata/artwork/f48519a2f5b2b80a3aa3a0ee7ba65bcee08b1c75319b23dd60c6f4ad51fe5701",
      wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", monitored:true, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Upgradable - subtitles complete",
      title: "Everything Everywhere All at Once", posterUrl: "/api/metadata/artwork/af01bb2154b830c4de7e04a885fe9f9a646ccf008bb8efb505b3000f44a920c6",
      wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:2 },
    { scenario: "Upgradable - subtitles short",
      title: "Ex Machina", posterUrl: "/api/metadata/artwork/6bb5fb60d2f89eb56a211efd703eed1c3d0ec927abe3949f8ee4b32402552e4d",
      wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:1 },
    { scenario: "Upgradable - no subtitles held",
      title: "Inception", posterUrl: "/api/metadata/artwork/b8859b643b9e36627948bc194975309f9d786c52fc99a44d90c9f654b310ae2b",
      wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Upgradable - Subber has not resolved it",
      title: "Interstellar", posterUrl: "/api/metadata/artwork/ced3868f1e43a568d74a72ff561dd38a149229ea2c4fa52c5e9a554c71029c65",
      wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", monitored:true, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Downloading - subtitles inherit the title",
      title: "Mad Max: Fury Road", posterUrl: "/api/metadata/artwork/28376be2186dbf463a77de61bff882a209f0c2f98d8d8f0331e12b8efc2e4782",
      wantedStatus:"downloading", hasFile:false, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Missing - subtitles inherit the title",
      title: "Sicario", posterUrl: "/api/metadata/artwork/881aa7ac6ea972e5a95f862c6bfcfee32e2fac1df53934eaca3416d63206356a",
      wantedStatus:"missing", hasFile:false, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Upcoming - subtitles inherit the title",
      title: "The Martian", posterUrl: "/api/metadata/artwork/fafbc3108d750c4b0aa5347b7069ec1a58c81e0ab0c0306059572099ce23cb4c",
      wantedStatus:"upcoming", hasFile:false, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Quality met - subtitles complete - NOT MONITORED",
      title: "Arrival", posterUrl: "/api/metadata/artwork/473be3f38acc67c4b8289452deeabbadd4f65746808c84eae1c4d06fd29b691c",
      wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:2 },
    { scenario: "Quality met - subtitles short - NOT MONITORED",
      title: "Big Buck Bunny", posterUrl: "/api/metadata/artwork/8caa0b5403699888fe15bc4b32e91244d74474ab5cb4bb3cd6e49a17235d7598",
      wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", monitored:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:1 },
    { scenario: "Quality met - no subtitles held - NOT MONITORED",
      title: "Blade Runner 2049", posterUrl: "/api/metadata/artwork/8225563f8dad6e6bb1fea1e451eed27ac0c67543ed01aab55ef4a139f8d54e5e",
      wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Quality met - Subber has not resolved it - NOT MONITORED",
      title: "Dune", posterUrl: "/api/metadata/artwork/f48519a2f5b2b80a3aa3a0ee7ba65bcee08b1c75319b23dd60c6f4ad51fe5701",
      wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", monitored:false, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Upgradable - subtitles complete - NOT MONITORED",
      title: "Everything Everywhere All at Once", posterUrl: "/api/metadata/artwork/af01bb2154b830c4de7e04a885fe9f9a646ccf008bb8efb505b3000f44a920c6",
      wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:2 },
    { scenario: "Upgradable - subtitles short - NOT MONITORED",
      title: "Ex Machina", posterUrl: "/api/metadata/artwork/6bb5fb60d2f89eb56a211efd703eed1c3d0ec927abe3949f8ee4b32402552e4d",
      wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", monitored:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:1 },
    { scenario: "Upgradable - no subtitles held - NOT MONITORED",
      title: "Inception", posterUrl: "/api/metadata/artwork/b8859b643b9e36627948bc194975309f9d786c52fc99a44d90c9f654b310ae2b",
      wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Upgradable - Subber has not resolved it - NOT MONITORED",
      title: "Interstellar", posterUrl: "/api/metadata/artwork/ced3868f1e43a568d74a72ff561dd38a149229ea2c4fa52c5e9a554c71029c65",
      wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", monitored:false, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Downloading - subtitles inherit the title - NOT MONITORED",
      title: "Mad Max: Fury Road", posterUrl: "/api/metadata/artwork/28376be2186dbf463a77de61bff882a209f0c2f98d8d8f0331e12b8efc2e4782",
      wantedStatus:"downloading", hasFile:false, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Missing - subtitles inherit the title - NOT MONITORED",
      title: "Sicario", posterUrl: "/api/metadata/artwork/881aa7ac6ea972e5a95f862c6bfcfee32e2fac1df53934eaca3416d63206356a",
      wantedStatus:"missing", hasFile:false, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Upcoming - subtitles inherit the title - NOT MONITORED",
      title: "The Martian", posterUrl: "/api/metadata/artwork/fafbc3108d750c4b0aa5347b7069ec1a58c81e0ab0c0306059572099ce23cb4c",
      wantedStatus:"upcoming", hasFile:false, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 }
  ],
  tv: [
    { scenario: "Quality met - subtitles complete",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:20 },
    { scenario: "Quality met - subtitles short",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:10 },
    { scenario: "Quality met - no subtitles held",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Quality met - Subber has not resolved it",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, monitored:true, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Continuing - subtitles complete",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"airing", airedEpisodeCount:8, airedWithFileCount:8, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:16 },
    { scenario: "Continuing - subtitles short",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"airing", airedEpisodeCount:8, airedWithFileCount:8, monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:8 },
    { scenario: "Continuing - no subtitles held",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"airing", airedEpisodeCount:8, airedWithFileCount:8, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Continuing - Subber has not resolved it",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"airing", airedEpisodeCount:8, airedWithFileCount:8, monitored:true, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Upgradable - subtitles complete",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:12 },
    { scenario: "Upgradable - subtitles short",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:6 },
    { scenario: "Upgradable - no subtitles held",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Upgradable - Subber has not resolved it",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, monitored:true, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Missing, part way - subtitles complete",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:6 },
    { scenario: "Missing, part way - subtitles short",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:3 },
    { scenario: "Missing, part way - no subtitles held",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Missing, part way - Subber has not resolved it",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, monitored:true, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Missing, none held - subtitles inherit the title",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"missing", airedEpisodeCount:29, airedWithFileCount:0, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Downloading - subtitles complete",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"downloading", airedEpisodeCount:12, airedWithFileCount:4, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:8 },
    { scenario: "Downloading - subtitles short",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"downloading", airedEpisodeCount:12, airedWithFileCount:4, monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:4 },
    { scenario: "Downloading - no subtitles held",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"downloading", airedEpisodeCount:12, airedWithFileCount:4, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Downloading - Subber has not resolved it",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"downloading", airedEpisodeCount:12, airedWithFileCount:4, monitored:true, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Upcoming - subtitles inherit the title",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"upcoming", airedEpisodeCount:0, airedWithFileCount:0, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Quality met - subtitles complete - NOT MONITORED",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:20 },
    { scenario: "Quality met - subtitles short - NOT MONITORED",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, monitored:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:10 },
    { scenario: "Quality met - no subtitles held - NOT MONITORED",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Quality met - Subber has not resolved it - NOT MONITORED",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, monitored:false, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Continuing - subtitles complete - NOT MONITORED",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"airing", airedEpisodeCount:8, airedWithFileCount:8, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:16 },
    { scenario: "Continuing - subtitles short - NOT MONITORED",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"airing", airedEpisodeCount:8, airedWithFileCount:8, monitored:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:8 },
    { scenario: "Continuing - no subtitles held - NOT MONITORED",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"airing", airedEpisodeCount:8, airedWithFileCount:8, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Continuing - Subber has not resolved it - NOT MONITORED",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"airing", airedEpisodeCount:8, airedWithFileCount:8, monitored:false, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Upgradable - subtitles complete - NOT MONITORED",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:12 },
    { scenario: "Upgradable - subtitles short - NOT MONITORED",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, monitored:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:6 },
    { scenario: "Upgradable - no subtitles held - NOT MONITORED",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Upgradable - Subber has not resolved it - NOT MONITORED",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, monitored:false, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Missing, part way - subtitles complete - NOT MONITORED",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:6 },
    { scenario: "Missing, part way - subtitles short - NOT MONITORED",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, monitored:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:3 },
    { scenario: "Missing, part way - no subtitles held - NOT MONITORED",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Missing, part way - Subber has not resolved it - NOT MONITORED",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, monitored:false, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Missing, none held - subtitles inherit the title - NOT MONITORED",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"missing", airedEpisodeCount:29, airedWithFileCount:0, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Downloading - subtitles complete - NOT MONITORED",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"downloading", airedEpisodeCount:12, airedWithFileCount:4, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:8 },
    { scenario: "Downloading - subtitles short - NOT MONITORED",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"downloading", airedEpisodeCount:12, airedWithFileCount:4, monitored:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:4 },
    { scenario: "Downloading - no subtitles held - NOT MONITORED",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"downloading", airedEpisodeCount:12, airedWithFileCount:4, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Downloading - Subber has not resolved it - NOT MONITORED",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"downloading", airedEpisodeCount:12, airedWithFileCount:4, monitored:false, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Upcoming - subtitles inherit the title - NOT MONITORED",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"upcoming", airedEpisodeCount:0, airedWithFileCount:0, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 }
  ]
};

/* The supplied Sonarr screenshot is a useful baseline, not a specification for
   Deluno. It gives us five quick colour readings; the comparison below keeps
   those readings visible while showing the extra facts the Deluno card carries.
   The colours are deliberately labelled as a visual reference, not imported as
   Deluno's semantic tokens. */
const SONARR_REFERENCE = [
  { key: "continuing", label: "Continuing", detail: "All episodes downloaded", colour: "#5d9cec", pct: 100, count: "8 / 8" },
  { key: "ended", label: "Ended", detail: "All episodes downloaded", colour: "#27c24c", pct: 100, count: "20 / 20" },
  { key: "missing-monitored", label: "Missing Episodes", detail: "Series monitored", colour: "#f05050", pct: 15, count: "3 / 20" },
  { key: "missing-unmonitored", label: "Missing Episodes", detail: "Series not monitored", colour: "#f0a000", pct: 15, count: "3 / 20" },
  { key: "downloading", label: "Downloading", detail: "One or more episodes", colour: "#7b4bb3", pct: 33, count: "4 / 12" }
];

/* NOT a state - a legibility test, kept apart so it cannot duplicate one. */
const STRESS = {
  movies: { scenario: "The longest strings this card must survive",
            title: "Everything Everywhere All at Once", posterUrl: "/api/metadata/artwork/af01bb2154b830c4de7e04a885fe9f9a646ccf008bb8efb505b3000f44a920c6",
            wantedStatus: "covered", hasFile: true, monitored: true,
            currentQuality: "Bluray-2160p Remux", subtitleLanguagesWanted: 12,
            subtitleLanguagesHeld: 12 },
  tv: { scenario: "The longest strings this card must survive",
        title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
        wantedStatus: "missing", monitored: true, airedEpisodeCount: 170,
        airedWithFileCount: 148, subtitleLanguagesWanted: 12, subtitleLanguagesHeld: 900 }
};

/* ══════════════════════════════════════════════════════════════
   Mount
   ══════════════════════════════════════════════════════════════ */
const WIDTHS = { sm: 108, md: 148, lg: 190 };

function mountDecider({ medium }) {
  const isShow = medium === "tv";
  const CONTROLS = controlsFor(medium);
  let DATA = { items: [], live: false, reason: "" };

  try {
    const h = new URLSearchParams(location.hash.slice(1));
    for (const k of Object.keys(S)) if (h.get(k)) S[k] = h.get(k);
  } catch (e) { /* leave the defaults */ }

  function settingsLine() {
    return CONTROLS.filter(c => c.key !== "theme" && c.key !== "size")
      .map(c => (c.user ? "[switch] " : "") + c.label + ": "
                + (c.opts.find(o => o[0] === S[c.key]) || ["", "?"])[1])
      .join("  ·  ");
  }

  function drawRail() {
    document.getElementById("rail").innerHTML = CONTROLS.map(c =>
      '<div class="grp"><label>' + c.label + '</label><div class="seg">'
      + c.opts.map(o => '<button data-k="' + c.key + '" data-v="' + o[0] + '" aria-pressed="'
          + (S[c.key] === o[0]) + '">' + o[1] + '</button>').join("")
      + '</div></div>').join("")
      + '<div class="readout"><code id="line">' + esc(settingsLine()) + '</code>'
      + '<button id="copy">Copy</button></div>';

    history.replaceState(null, "", "#" + Object.keys(S).map(k => k + "=" + S[k]).join("&"));
    /*
      `navigator.clipboard` exists only in a secure context, and the rig is plain
      HTTP — so on the one machine this page is actually used from, the API is
      undefined and the button did nothing at all, silently. James: "copy button
      doesnt work". The execCommand path is deprecated and works everywhere; it is
      the fallback precisely because the modern API is the one that is unavailable
      here.
    */
    document.getElementById("copy").onclick = () => {
      const text = medium.toUpperCase() + " — " + settingsLine() + " — " + location.href;
      const done = ok => { const b = document.getElementById("copy");
        b.textContent = ok ? "Copied" : "Press Ctrl+C";
        if (!ok) { const r = document.createRange(); r.selectNodeContents(document.getElementById("line"));
                   const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r); }
        setTimeout(() => { b.textContent = "Copy"; }, 1800); };
      const legacy = () => {
        const ta = document.createElement("textarea");
        ta.value = text; ta.style.cssText = "position:fixed;top:-1000px;opacity:0";
        document.body.appendChild(ta); ta.select();
        let ok = false;
        try { ok = document.execCommand("copy"); } catch (e) { ok = false; }
        ta.remove(); done(ok);
      };
      if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(text).then(() => done(true), legacy);
      } else legacy();
    };
  }

  function drawPresets() {
    const el = document.getElementById("presets");
    if (!el) return;
    /* Both presets set `fill` and `rem`. On the movie shelf `fill` does nothing,
       so a preset there would be half a control pretending to be a whole one. */
    if (!isShow) { el.innerHTML = ""; return; }
    const match = PRESETS.find(p => Object.keys(p.set).every(k => S[k] === p.set[k]));
    el.innerHTML = '<span>Presets</span>'
      + PRESETS.map((p, i) => '<button data-preset="' + i + '">' + p.label + '</button>').join("")
      + '<em>' + esc(match ? match.why : "a combination of your own") + '</em>';
  }

  function drawBanner() {
    const el = document.getElementById("banner");
    const noun = isShow ? "shows" : "films";
    if (DATA.live) {
      el.className = "banner ok";
      el.innerHTML = "Your real library is drawn below the catalogue — <b>" + DATA.items.length
        + " " + noun + "</b>, with their own artwork and counts.";
      return;
    }
    el.className = "banner warn";
    const why = DATA.reason === "notab" ? "This tab is not signed in."
      : DATA.reason === "empty" ? "The library came back empty."
      : "The library could not be read (" + esc(DATA.reason) + ").";
    el.innerHTML = "<b>Catalogue only.</b> " + why
      + " Every scenario is drawn below regardless — a real library cannot be relied on to"
      + " contain a downloading or unmonitored title when you look. To see yours as well:"
      + " from your signed-in Deluno tab, paste this page's address into <b>that same tab</b>"
      + " and press enter. The session token lives in that tab only.";
  }

  function drawArtworkSource() {
    const el = document.getElementById("artwork-source");
    if (!el) return;
    const seen = new Set();
    const sourceItems = [];
    for (const item of (DATA.items.length ? DATA.items : SCENARIOS[medium])) {
      const title = typeof item.title === "string" && item.title.trim() ? item.title.trim() : "Untitled";
      if (seen.has(title) || !item.posterUrl) continue;
      seen.add(title);
      sourceItems.push({ ...item, title });
      if (sourceItems.length === 6) break;
    }
    const live = DATA.items.length > 0;
    el.innerHTML = '<div class="artwork-head">'
      + '<div><b>' + (live ? "Live lab artwork" : "Reference artwork") + '</b>'
      + '<span>' + (live
        ? "The first six TV posters currently in Deluno Lab."
        : "The render uses TV reference posters until it can read the signed-in lab library.") + '</span></div>'
      + '<code>' + esc(RENDER_ORIGIN) + '</code>'
      + '</div>'
      + '<div class="artwork-strip">'
      + sourceItems.map(item => '<figure><div class="art artwork-preview">'
        + '<img loading="lazy" data-artwork src="' + esc(renderUrl(item.posterUrl)) + '" alt="' + esc(item.title + " artwork") + '">'
        + '</div><figcaption title="' + esc(item.title) + '">' + esc(item.title) + '</figcaption></figure>').join("")
      + '</div>';
  }

  function wireArtworkFallbacks() {
    document.querySelectorAll("img[data-artwork]").forEach(img => {
      const fallback = () => {
        if (!img.isConnected) return;
        const placeholder = document.createElement("div");
        placeholder.className = "noart";
        placeholder.textContent = "Artwork unavailable";
        img.replaceWith(placeholder);
      };
      if (img.complete && img.naturalWidth === 0) fallback();
      else img.addEventListener("error", fallback, { once: true });
    });
  }

  function sonarrBarHtml(sample) {
    return '<div class="sonarr-progress" style="--sonarr-colour:' + esc(sample.colour)
      + ';--sonarr-pct:' + sample.pct + '%">'
      + '<div class="sonarr-fill"></div><span>' + esc(sample.count) + '</span></div>';
  }

  function sonarrPosterHtml(item, sample) {
    return '<figure class="compare-card sonarr-sample"><div class="sonarr-poster">'
      + (item.posterUrl
        ? '<img loading="lazy" data-artwork src="' + esc(renderUrl(item.posterUrl)) + '" alt="' + esc(item.title + " artwork") + '">'
        : '<div class="noart">no art</div>')
      + sonarrBarHtml(sample)
      + '</div><figcaption><b>' + esc(sample.label) + '</b><span>' + esc(sample.detail) + '</span></figcaption></figure>';
  }

  function delunoProposalHtml(item, label, detail) {
    return '<figure class="compare-card deluno-sample"><div class="deluno-card-frame">'
      + cardHtml(item, true)
      + '</div><figcaption><b>' + esc(label) + '</b><span>' + esc(detail) + '</span></figcaption></figure>';
  }

  function drawComparison() {
    const el = document.getElementById("comparison");
    if (!el || !isShow) return;

    const findScenario = phrase => SCENARIOS.tv.find(it => it.scenario.includes(phrase)) || SCENARIOS.tv[0];
    const sonarrItems = [
      [findScenario("Continuing - subtitles short"), SONARR_REFERENCE[0]],
      [findScenario("Quality met - subtitles complete"), SONARR_REFERENCE[1]],
      [findScenario("Missing, part way - subtitles short"), SONARR_REFERENCE[2]],
      [findScenario("Downloading - subtitles short"), SONARR_REFERENCE[4]]
    ];
    const delunoItems = [
      [findScenario("Missing, none held"), "Missing at zero coverage", "0 / 29 aired · SUBS Missing"],
      [findScenario("Missing, part way - subtitles short"), "Missing", "3 / 20 aired · SUBS 3 / 9"],
      [findScenario("Continuing - subtitles short"), "Continuing", "8 / 8 aired · SUBS 8 / 24"],
      [findScenario("Quality met - subtitles complete"), "Quality met", "10 / 10 aired · SUBS 20 / 20"]
    ];
    const saved = { rem: S.rem, fill: S.fill };
    let proposalCards = "";
    try {
      /* The comparison is deliberately fixed to the approved Deluno grammar:
         Missing red remainder, held green fill, and gold when quality is met.
         The controls below still let us compare the Sonarr alternative. */
      S.rem = "missing";
      S.fill = "held";
      proposalCards = delunoItems.map(([item, label, detail]) => delunoProposalHtml(item, label, detail)).join("");
    } finally {
      S.rem = saved.rem;
      S.fill = saved.fill;
    }

    const recommendedPalette = [
      { mark: "miss", label: "Missing", colour: SURFACES.deep.both.miss, note: "Movies" },
      { mark: "down", label: "Downloading", colour: SURFACES.deep.both.down, note: "Movies" },
      { mark: "upg", label: "Upgradable", colour: SURFACES.deep.both.upg, note: "Movies" },
      { mark: "gold", label: "Quality met", colour: SURFACES.deep.both.gold, note: "Movies" },
      { mark: "soon", label: "Upcoming", colour: SURFACES.deep.both.soon, note: "Movies" },
      { mark: "cont", label: "Continuing", colour: CONT_CANDIDATES.magenta.hsl, note: "TV only" }
    ];
    const paletteHtml = recommendedPalette.map(item =>
      '<span style="background:hsl(' + item.colour + ');color:' + labelOn(item.mark) + '">'
      + '<b>' + esc(item.label) + '</b><small>' + esc(item.note) + '</small></span>').join("");

    el.innerHTML = '<div class="comparison-heading">'
      + '<div><span class="eyebrow">Sonarr reference → recommended Deluno direction</span>'
      + '<h2 id="comparison-title">Recommendation: keep Sonarr\'s instant read. Add Deluno\'s missing facts.</h2>'
      + '<p>Sonarr gives the shelf a fast colour legend. Deluno keeps that glanceable signal while separating the facts Sonarr compresses into it.</p></div>'
      + '<span class="decision-badge">Recommended · renderer only</span></div>'
      + '<div class="palette-rule"><div class="palette-copy"><span class="eyebrow">Palette rule</span>'
      + '<b>Reuse the movie colours wherever the state matches.</b>'
      + '<small>Continuing remains a TV-only state in the legend; coverage fill follows held/quality semantics.</small></div>'
      + '<div class="palette-swatches">' + paletteHtml + '</div></div>'
      + '<div class="comparison-grid">'
      + '<article class="comparison-panel sonarr-reference">'
      + '<span class="eyebrow">From the supplied screenshot</span>'
      + '<h3>Sonarr\'s five-state legend</h3>'
      + '<p class="comparison-copy">One coloured reading tells you the series state and whether the series is complete, missing, or arriving.</p>'
      + '<ul class="sonarr-legend">'
      + SONARR_REFERENCE.map(item => '<li><span class="sonarr-swatch" style="background:' + esc(item.colour) + '"></span>'
        + '<span><b>' + esc(item.label) + '</b><small>' + esc(item.detail) + '</small></span></li>').join("")
      + '</ul>'
      + '<div class="sonarr-samples">' + sonarrItems.map(([item, sample]) => sonarrPosterHtml(item, sample)).join("") + '</div>'
      + '</article>'
      + '<article class="comparison-panel deluno-proposal">'
      + '<span class="eyebrow">Recommendation based on DESIGN-006</span>'
      + '<h3>Deluno enhanced TV card</h3>'
      + '<p class="comparison-copy">The state stays immediate, but the card also tells you coverage of aired episodes and subtitle coverage without using a second status legend.</p>'
      + '<div class="deluno-samples">' + proposalCards + '</div>'
      + '<p class="proposal-callout"><b>Implemented renderer settings:</b> Deep · movie palette for matching states · Missing red remainder · held green fill · Quality met gold when complete · episode count on · subtitle count on · <b>SUBS</b> only. Unmonitored is always the flat grey override; <b>Upcoming</b> and <b>Downloading</b> stay distinct. This direction is now applied to the product TV shelf.</p>'
      + '</article>'
      + '</div>'
      + '<table class="compare-table"><thead><tr><th>Sonarr compresses</th><th>Deluno keeps separate</th></tr></thead><tbody>'
      + '<tr><td>Continuing / Ended / Missing / Downloading</td><td><b>Action state</b> — Continuing, Quality met, Upgradable, Missing, Downloading, or Upcoming.</td></tr>'
      + '<tr><td>One episode-progress signal</td><td><b>Top bar</b> — aired episodes held, with the fill measuring coverage of what has aired.</td></tr>'
      + '<tr><td>No subtitle signal in the legend</td><td><b>Bottom bar</b> — SUBS held / wanted, counted across episodes actually held.</td></tr>'
      + '<tr><td>Not monitored gets another status colour</td><td><b>Grey override</b> — both bars are flat grey, taking priority over Missing red or any held fill.</td></tr>'
      + '<tr><td>Ended means all episodes downloaded</td><td><b>No duplicate lifecycle colour</b> — a finished show can still be Missing or Upgradable, so the card keeps the actionable state.</td></tr>'
      + '</tbody></table>';
  }

  function clearanceHtml() {
    if (!isShow) return "";
    const rows = Object.keys(CONT_CANDIDATES).map(k => {
      const c = CONT_CANDIDATES[k], n = nearestTo(c.hsl), wc = whiteOn(c.hsl);
      return '<tr' + (k === S.cont ? ' class="on"' : '') + '>'
        + '<td><span class="lstrip" style="background:hsl(' + c.hsl + ')"></span>' + c.label + '</td>'
        + '<td class="num">' + n.dE.toFixed(1) + '</td><td>' + n.name + '</td>'
        + '<td class="num' + (wc < 4.5 ? ' bad' : '') + '">' + wc.toFixed(2) + '</td></tr>';
    }).join("");
    return '<table class="clear"><thead><tr><th>Continuing</th><th>Clear by</th>'
      + '<th>of its nearest rung</th><th>White label</th></tr></thead><tbody>' + rows + '</tbody></table>';
  }

  function legendHtml() {
    const C = surfaces();
    return '<div class="legend">' + LADDER_FOR[medium].map(k =>
      '<span class="lchip"><span class="lstrip" style="background:hsl(' + C[k] + ')"></span>'
      + MARKS[k] + '</span>').join("")
      + '<span class="lchip"><span class="lstrip" style="background:' + UNMONITORED.track + '"></span>Unmonitored</span>'
      + '</div>';
  }

  /*
    All four switch combinations at once.

    The two text switches are user options, so every one of these four is a card
    a real person will see. Leaving them to be found by flipping is how an
    off-state ships unlooked-at — and the off-state is exactly where a design
    that leans on its label falls over.
  */
  function matrixHtml() {
    const item = SCENARIOS[medium].find(it => markFor(it) === "miss" && it.monitored !== false)
      || SCENARIOS[medium][0];
    if (!item) return "";
    const saveM = S.media, saveS = S.subs;
    const cells = [
      ["on", "on", "both on"],
      ["on", "off", (isShow ? "episode count" : "quality") + " only"],
      ["off", "on", "subtitles only"],
      ["off", "off", "both off — as Deluno ships today"]
    ].map(([m, sub, caption]) => {
      S.media = m; S.subs = sub;
      return '<figure><div style="width:' + WIDTHS[S.size] + 'px">' + cardHtml(item, isShow)
        + '</div><figcaption>' + caption + '</figcaption></figure>';
    }).join("");
    S.media = saveM; S.subs = saveS;
    return '<h2 class="mx">Every switch combination <small>' + esc(item.title)
      + ', the same card, all four states a person can put it in</small></h2>'
      + '<div class="matrix">' + cells + '</div>';
  }

  function wallHtml(items) {
    return '<div class="wall" style="grid-template-columns: repeat(auto-fill, minmax('
      + WIDTHS[S.size] + 'px, ' + (WIDTHS[S.size] + 40) + 'px));">'
      + items.map(it => cardHtml(it, isShow, true)).join("") + '</div>';
  }

  function drawShelf() {
    const noun = isShow ? "show" : "film";
    /* The catalogue is drawn whether or not there is a live library: a real one
       cannot be relied on to contain a downloading title or an unmonitored one
       at the moment you happen to look, and those are the cards a design fails
       on. The live library is drawn as itself, underneath. */
    let html = clearanceHtml() + legendHtml() + matrixHtml()
      + '<h2 class="mx">Every combination <small>' + SCENARIOS[medium].length + ' cards — every '
      + 'rung, times every subtitle state, times monitored and not. No two draw the '
      + 'same thing.</small></h2>'
      + wallHtml(SCENARIOS[medium])
      + '<h2 class="mx">Longest strings <small>not a state — a legibility test, kept '
      + 'apart so it cannot duplicate one</small></h2>'
      + wallHtml([STRESS[medium]]);

    if (DATA.items.length) {
      html += '<h2 class="mx">Your library <small>' + DATA.items.length + ' '
        + noun + (DATA.items.length === 1 ? '' : 's') + ', as they actually are</small></h2>'
        + wallHtml(DATA.items);
    }
    document.getElementById("shelf").innerHTML = html;
  }

  function drawAll() {
    document.body.className = S.theme === "light" ? "light" : "";
    drawRail(); drawPresets(); drawBanner(); drawComparison(); drawArtworkSource(); drawShelf(); wireArtworkFallbacks();
  }

  document.addEventListener("click", e => {
    const preset = e.target.closest("button[data-preset]");
    if (preset) { Object.assign(S, PRESETS[+preset.dataset.preset].set); drawAll(); return; }
    const b = e.target.closest("button[data-k]");
    if (!b) return;
    S[b.dataset.k] = b.dataset.v;
    drawAll();
  });

  async function loadLive() {
    let token = null;
    try { token = sessionStorage.getItem("deluno-auth-token"); } catch (e) {}
    if (!token) { DATA.reason = "notab"; return; }
    const path = isShow ? "/api/series/page" : "/api/movies/page";
    try {
      const url = new URL(path + "?pageSize=60&sort=title&direction=asc", RENDER_ORIGIN);
      const res = await fetch(url,
                             { headers: { Authorization: "Bearer " + token } });
      if (!res.ok) throw res.status;
      const page = await res.json();
      if ((page.items || []).length) DATA = { items: page.items, live: true, reason: "" };
      else DATA.reason = "empty";
    } catch (err) { DATA.reason = "err:" + err; }
  }

  drawAll();
  loadLive().then(drawAll);

  /* Exposed so the page can be checked from the console rather than by eye. */
  window.__decider = { S, CONTROLS, surfaces, nearestTo, whiteOn, deltaE, medium,
    renderOrigin: RENDER_ORIGIN };
}
