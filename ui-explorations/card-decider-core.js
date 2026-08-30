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
  deep:  { both: { miss:"356 84% 41%", down:"214 94% 40%", upg:"150 90% 25%", cont:"178 96% 24%", gold:"49 100% 62%", soon:"270 76% 47%" } },
  jewel: { both: { miss:"352 88% 34%", down:"219 96% 33%", upg:"156 94% 20%", cont:"182 100% 19%", gold:"49 100% 62%", soon:"272 84% 39%" } }
};

/* The text colour for a count or a word on a ground — per theme, and NOT the
   surface. The deep surfaces used as text fail AA on the dark card. */
const TEXT = {
  dark:  { miss:"0 84% 62%", down:"207 96% 62%", upg:"145 78% 52%", cont:"178 76% 50%", gold:"44 98% 58%", soon:"268 82% 72%" },
  light: { miss:"0 84% 48%", down:"207 92% 45%", upg:"145 72% 34%", cont:"178 74% 30%", gold:"42 96% 40%", soon:"268 62% 50%" }
};

/* Gold is floored at 52% lightness, so white can never sit on it. Every other
   bar takes white; gold takes near-black. Forced, not a preference. */
const labelOn = mark => mark === "gold" ? "hsl(40 90% 12%)" : "hsl(0 0% 100%)";

/*
  Monitoring gets its own mark, and the bars keep their colour.

  The road here is worth keeping. DESIGN-001 gave an unmonitored title a half-grey
  **dot**; an earlier revision of this render halved the **bar's fill** instead.
  James: *"I think the half was in reference to the dots which we have removed"* —
  right, and the measurement agreed before the argument did: on a Missing title,
  whose fill is 0% wide, the half rendered as nothing at all. A half works on a dot
  because a dot has no length of its own; a bar IS a length, and that length already
  means the fraction you hold.

  Then: *"ditch the half and just overright it when its unmonitored its just black or
  grey period"* — which worked, but spent the bar's colour on a fact that is not
  about the title's state. And finally: *"maybe a nice little monitored /
  unmonitored on the poster is the best play here"*. That is the right answer. The
  bars go on saying what the title IS; whether Deluno is watching it is a separate
  fact and gets a separate mark.

  **Shown only when it is OFF, by default.** Nearly every title is monitored, so a
  badge on every card is a badge that says nothing on almost all of them — and the
  one card that needs to stand out stops standing out. An exception is worth marking;
  a default is not. Both are drawn so the choice can be looked at.
*/
/*
  The glyph.

  **Deluno already has one for monitoring: a shield.** `library-grid.tsx` draws
  ShieldCheck / ShieldOff on the line under the poster. This render reached for an
  eye — which reads as "watching" in English but is not the app's own word for it,
  and a second icon for a fact that already has one is how an icon language stops
  being a language. The shield is the default here for that reason; the others are
  drawn so the choice can be looked at rather than assumed.
*/
const GLYPHS = {
  shield: {
    on: '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M9 12l2 2 4-4"/>',
    off: '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M2 2l20 20"/>'
  },
  eye: {
    on: '<path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z"/><circle cx="12" cy="12" r="3"/>',
    off: '<path d="M2 2l20 20"/><path d="M6.7 6.8A10.6 10.6 0 0 0 1 12s4 7 11 7a10.4 10.4 0 0 0 5.3-1.4"/>'
       + '<path d="M9.9 5.2A10.9 10.9 0 0 1 12 5c7 0 11 7 11 7a19 19 0 0 1-3.2 4.2"/>'
       + '<path d="M9.9 9.9a3 3 0 0 0 4.2 4.2"/>'
  },
  bell: {
    on: '<path d="M18 8a6 6 0 0 0-12 0c0 7-3 8-3 8h18s-3-1-3-8"/><path d="M10.3 21a2 2 0 0 0 3.4 0"/>',
    off: '<path d="M18 8a6 6 0 0 0-9.3-5"/><path d="M6 8c0 7-3 8-3 8h13"/>'
       + '<path d="M10.3 21a2 2 0 0 0 3.4 0"/><path d="M2 2l20 20"/>'
  }
};
const glyph = monitored => '<svg viewBox="0 0 24 24" width="11" height="11" fill="none"'
  + ' stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"'
  + ' aria-hidden="true">' + GLYPHS[S.glyph][monitored ? "on" : "off"] + '</svg>';

/* On the artwork, in the corner. Opaque, because a translucent badge is a
   different colour on every poster — the same problem that killed the label. */
function monitorBadge(monitored) {
  if (S.mon === "off") return "";
  if (S.mon === "when-off" && monitored) return "";
  /* On a card the badge is not a control, so it states the fact and stops. The
     "click to …" half of the tooltip belongs on the detail pages, where the same
     glyph IS pressable. One vocabulary, two jobs. */
  return '<div class="mbadge' + (monitored ? '' : ' off') + '" title="'
    + (monitored ? 'Monitored' : 'Not monitored') + '">'
    + glyph(monitored) + '</div>';
}

const MONITOR_LINE = monitored => '<div class="mon' + (monitored ? '' : ' off') + '">'
  + (monitored ? '&#9679;' : '&#9675;') + ' ' + (monitored ? 'Monitored' : 'Not monitored') + '</div>';

/* ── Continuing's hue: a TV-only question ─────────────────────── */
const CONT_CANDIDATES = {
  teal:    { label: "Teal 178",    hsl: "178 96% 24%" },
  cyan:    { label: "Cyan 192",    hsl: "192 95% 27%" },
  magenta: { label: "Magenta 318", hsl: "318 78% 38%" },
  pink:    { label: "Pink 330",    hsl: "330 78% 40%" },
  lime:    { label: "Lime 92",     hsl: "92 88% 24%" },
  steel:   { label: "Steel",       hsl: "200 26% 40%" }
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
  mon: "when-off",     // when-off | always | line | off — how monitoring is shown
  glyph: "shield",     // shield | eye | bell — the app already uses a shield
  rem: "neutral",      // neutral | missing
  fill: "state",       // state | held
  cont: "magenta",     // TV only
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
    /* This IS the `showMonitored` poster option. Choosing the badge changes what
       that switch controls — it drew a line under the poster, and a line plus a
       badge would be the same fact twice on one card. */
    { key: "mon",    label: "Monitoring", opts: [["when-off","Badge when off"],["always","Badge always"],["line","Line under"],["off","Not shown"]], user: true },
    { key: "glyph",  label: "Glyph", opts: [["shield","Shield"],["eye","Eye"],["bell","Bell"]] },
    { key: "rem",    label: "Track",  opts: [["neutral","Neutral grey"],["missing","Missing red"]] },
    { key: "fill",   label: "Fill",   opts: [["state","State colour"],["held","What you hold"]] }
  ];
  /* The one control the movie shelf must not carry. */
  if (medium === "tv") base.push({ key: "cont", label: "Continuing", opts: CONT_OPTS });
  base.push({ key: "size", label: "Card", opts: [["sm","Small"],["md","Medium"],["lg","Large"]] });
  return base;
}

const PRESETS = [
  { label: "Sonarr's grammar", why: "state fill over a neutral track — colour is the state, length is the fraction",
    set: { fill: "state", rem: "neutral" } },
  { label: "Composition, like SUBS", why: "no neutral anywhere — every segment is what that part IS",
    set: { fill: "held", rem: "missing" } }
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

  James: *"upcoming is the wrong colour, its missing yes but its upcoming too"*.

  He is right and this was mine: the subtitle bar's empty state was hardcoded to
  Missing, so an Upcoming film read "SUBS Missing" in red. **You cannot be missing
  a subtitle for a file that cannot exist yet.** Missing means *it is out and you
  do not have it* — that is the whole of what separates it from Upcoming on the
  ladder, and the subtitle bar was throwing the distinction away.

  So when a title holds no files at all, the subtitle bar inherits the title's own
  reason for having none: Upcoming stays Upcoming, Downloading stays Downloading,
  and only a title that really is out and really is absent reads Missing.

  Once there IS a file, the bar is about subtitles again, and a language Subber
  has not found for a file you hold is genuinely missing.
*/
function subtitleState(subs, mark) {
  if (subs.files === 0) return mark;   /* no file — the title's reason is the subtitle's reason */
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
    ? "No files yet, so the subtitle bar carries the same state rather than claiming Missing."
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
    /* Both layers are the same string in the same place. Only the paint of the
       front one is clipped — sizing-and-centring it instead makes the label
       slide sideways as the bar fills. */
    + '<div class="txt" style="color:' + onRem + '"><span>' + inner + '</span></div>'
    + '<div class="txt" style="color:' + onFill + ';clip-path:inset(0 ' + (100 - pct) + '% 0 0)">'
    + '<span>' + inner + '</span></div>'
    + '</div>';
}

/*
  The track.

  Measured in Sonarr's own DOM, every poster, no exceptions: the track is neutral
  grey and the fill is the state's colour filled to the fraction held. Colour says
  the state, length says how much, and neither is lost — precisely because the
  track is neutral. Painting the track Missing red is what made a Missing title's
  fill and track the same colour, so its fraction vanished.

  At 0% fill a neutral bar would say its state nowhere, and Deluno has deleted the
  corner pill that used to carry it. So the track's label wears the state's own
  TEXT colour — the text token, not the surface, because surfaces are tuned for
  white-on-bar and text tokens for reading on a ground.
*/
function track(mark) {
  if (S.rem === "neutral") return { colour: "var(--idle)", label: "hsl(" + TEXT[S.theme][mark] + ")" };
  return { colour: "hsl(" + surfaces().miss + ")", label: "hsl(0 0% 100%)" };
}

function fillColourFor(mark, fullyHeld, C) {
  if (S.fill === "held") return "hsl(" + (fullyHeld && mark === "gold" ? C.gold : C.upg) + ")";
  return "hsl(" + C[mark] + ")";
}

function cardHtml(it, isShow, withCaption) {
  const C = surfaces(), mark = markFor(it);
  const media = mediaBar(it, isShow);
  const subs = subtitleBar(it, isShow);
  const subPct = subs.wanted ? Math.round(subs.held / subs.wanted * 100) : 0;
  /* "None asked for" is not a state: subtitles Subber has not resolved are
     simply not here, and not here is Missing. No denominator to print, so the
     bar prints the word — as a film's media bar does with no quality to name. */
  const subState = subtitleState(subs, mark);
  const subLabel = subs.wanted ? subs.held + " / " + subs.wanted : MARKS[subState];

  const T = track(mark), TS = track(subState);

  /* A bar with no fraction keeps its state's colour under either grammar: an
     Upcoming title has not started, a downloading one has no held part yet. */
  const topFillFlat = media.fraction ? fillColourFor(mark, media.pct === 100, C) : "hsl(" + C[mark] + ")";
  const topFill = topFillFlat;
  const topOnFill = topFillFlat === "hsl(" + C.gold + ")" ? labelOn("gold") : "hsl(0 0% 100%)";

  const subSettled = subs.wanted > 0 && subs.held === subs.wanted;
  /* With no files there is nothing held, so the bar carries the title's own
     state at full width rather than an empty green one. */
  const subNoFiles = subs.files === 0;
  const subPctDrawn = subNoFiles ? (subState === "miss" ? 0 : 100) : subPct;
  const subFillFlat = subNoFiles ? "hsl(" + C[subState] + ")"
    : subSettled ? "hsl(" + C.gold + ")" : "hsl(" + C.upg + ")";
  const subFill = subFillFlat;
  const subOnFill = subNoFiles ? labelOn(subState)
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
    + (it.posterUrl ? '<img loading="lazy" src="' + esc(it.posterUrl) + '" alt="">'
                    : '<div class="noart">no art</div>')
    + '</div>';


  /* No corner pill, and the bars are always on the artwork — both settled:
     "corner pill is a complete removal and bars always on artwork". */
  /* The monitoring line is not part of this design decision — it is an existing
     poster option with its own switch — but it is drawn here because it is now
     the ONLY thing that says a title is unmonitored, and a render that omits it
     would make those scenarios look identical to the monitored ones. */
  const monitored = it.monitored !== false;
  const card = '<div class="card">' + monitorBadge(monitored) + topBar + art + botBar
    + (S.mon === "line" ? MONITOR_LINE(monitored) : '') + '</div>';
  if (!withCaption) return card;

  const note = barNote(media, subs, mark, isShow);
  const halfNote = it.monitored === false
    ? " Deluno is not watching this one — its own mark, so the bars go on saying what the"
      + " title is."
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
/* Real titles and real artwork, served from Deluno's own /api/metadata/artwork
   proxy, which needs no auth — so the catalogue looks like the shelf whether or
   not the page is signed in. James: "put some artwork in as well so we can see
   it for real". A card judged against a grey rectangle is not judged: the whole
   point of a bar on a poster is how it sits on a picture. */
const SCENARIOS = {
  movies: [
    { scenario: "At the cutoff, subtitles complete",
      title: "Blade Runner 2049", posterUrl: "/api/metadata/artwork/8225563f8dad6e6bb1fea1e451eed27ac0c67543ed01aab55ef4a139f8d54e5e",
      wantedStatus:"covered", hasFile:true, monitored:true, currentQuality:"Remux-2160p", subtitleLanguagesWanted:3, subtitleLanguagesHeld:3 },
    { scenario: "Below the cutoff, subtitles short",
      title: "Arrival", posterUrl: "/api/metadata/artwork/473be3f38acc67c4b8289452deeabbadd4f65746808c84eae1c4d06fd29b691c",
      wantedStatus:"upgrade", hasFile:true, monitored:true, currentQuality:"WEBDL-1080p", subtitleLanguagesWanted:3, subtitleLanguagesHeld:1 },
    { scenario: "Held, no subtitles at all yet",
      title: "Sicario", posterUrl: "/api/metadata/artwork/881aa7ac6ea972e5a95f862c6bfcfee32e2fac1df53934eaca3416d63206356a",
      wantedStatus:"upgrade", hasFile:true, monitored:true, currentQuality:"WEBRip-720p", subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Held, Subber has not resolved it",
      title: "Ex Machina", posterUrl: "/api/metadata/artwork/6bb5fb60d2f89eb56a211efd703eed1c3d0ec927abe3949f8ee4b32402552e4d",
      wantedStatus:"covered", hasFile:true, monitored:true, currentQuality:"Bluray-1080p", subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "Bytes moving now",
      title: "Dune", posterUrl: "/api/metadata/artwork/f48519a2f5b2b80a3aa3a0ee7ba65bcee08b1c75319b23dd60c6f4ad51fe5701",
      wantedStatus:"downloading", hasFile:false, monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:0 },
    { scenario: "Out, and not here",
      title: "Inception", posterUrl: "/api/metadata/artwork/b8859b643b9e36627948bc194975309f9d786c52fc99a44d90c9f654b310ae2b",
      wantedStatus:"missing", hasFile:false, monitored:true, subtitleLanguagesWanted:3, subtitleLanguagesHeld:0 },
    { scenario: "Out, not here, NOT monitored",
      title: "The Martian", posterUrl: "/api/metadata/artwork/fafbc3108d750c4b0aa5347b7069ec1a58c81e0ab0c0306059572099ce23cb4c",
      wantedStatus:"missing", hasFile:false, monitored:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:0 },
    { scenario: "Not released yet",
      title: "Interstellar", posterUrl: "/api/metadata/artwork/ced3868f1e43a568d74a72ff561dd38a149229ea2c4fa52c5e9a554c71029c65",
      wantedStatus:"upcoming", hasFile:false, monitored:true, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Not released, NOT monitored",
      title: "Mad Max: Fury Road", posterUrl: "/api/metadata/artwork/28376be2186dbf463a77de61bff882a209f0c2f98d8d8f0331e12b8efc2e4782",
      wantedStatus:"upcoming", hasFile:false, monitored:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Held below cutoff, NOT monitored",
      title: "Big Buck Bunny", posterUrl: "/api/metadata/artwork/8caa0b5403699888fe15bc4b32e91244d74474ab5cb4bb3cd6e49a17235d7598",
      wantedStatus:"upgrade", hasFile:true, monitored:false, currentQuality:"WEBDL-2160p", subtitleLanguagesWanted:2, subtitleLanguagesHeld:2 },
    { scenario: "The longest strings this card must survive",
      title: "Everything Everywhere All at Once", posterUrl: "/api/metadata/artwork/af01bb2154b830c4de7e04a885fe9f9a646ccf008bb8efb505b3000f44a920c6",
      wantedStatus:"covered", hasFile:true, monitored:true, currentQuality:"Bluray-2160p Remux", subtitleLanguagesWanted:12, subtitleLanguagesHeld:12 }
  ],
  tv: [
    { scenario: "Part of the way through",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"missing", monitored:true, airedEpisodeCount:20, airedWithFileCount:3, subtitleLanguagesWanted:2, subtitleLanguagesHeld:4 },
    { scenario: "Every aired episode held, more to come",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"airing", monitored:true, airedEpisodeCount:8, airedWithFileCount:8, subtitleLanguagesWanted:2, subtitleLanguagesHeld:11 },
    { scenario: "Ended, complete, at the cutoff",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"covered", monitored:true, airedEpisodeCount:12, airedWithFileCount:12, subtitleLanguagesWanted:2, subtitleLanguagesHeld:24 },
    { scenario: "Complete, below the cutoff",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"upgrade", monitored:true, airedEpisodeCount:6, airedWithFileCount:6, subtitleLanguagesWanted:1, subtitleLanguagesHeld:4 },
    { scenario: "Every aired episode held, no subtitles yet",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"airing", monitored:true, airedEpisodeCount:10, airedWithFileCount:10, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Aired, and none of it held",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"missing", monitored:true, airedEpisodeCount:40, airedWithFileCount:0, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Bytes moving now",
      title: "Severance", posterUrl: "/api/metadata/artwork/3839bfc8c1e2bb20cf97f204ebf8d8009f37adbc6a2979ce57335c45821046a5",
      wantedStatus:"downloading", monitored:true, airedEpisodeCount:20, airedWithFileCount:4, subtitleLanguagesWanted:2, subtitleLanguagesHeld:6 },
    { scenario: "Nothing has aired yet",
      title: "The Bear", posterUrl: "/api/metadata/artwork/fee2ac574c6f38ea074ba8128e921b4dee8ba1330de8f35458fdad73a7b12ffc",
      wantedStatus:"upcoming", monitored:true, airedEpisodeCount:0, airedWithFileCount:0, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Nothing aired, NOT monitored",
      title: "Andor", posterUrl: "/api/metadata/artwork/5c494a4b5cd53928dd0dc5409f68d8422697866e85a38840e609fb1ff6a31117",
      wantedStatus:"upcoming", monitored:false, airedEpisodeCount:0, airedWithFileCount:0, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { scenario: "Part of the way through, NOT monitored",
      title: "Slow Horses", posterUrl: "/api/metadata/artwork/23a4188c5777fc1fbeb37287ebae21c1825d2586f06c1e5983082853864607f5",
      wantedStatus:"missing", monitored:false, airedEpisodeCount:60, airedWithFileCount:22, subtitleLanguagesWanted:2, subtitleLanguagesHeld:30 },
    { scenario: "Held, Subber has not resolved it",
      title: "Shogun", posterUrl: "/api/metadata/artwork/c507eac5b0ccba375facac9262a7a4e58d9e0a9898d8419f5faad6540503bdbd",
      wantedStatus:"covered", monitored:true, airedEpisodeCount:34, airedWithFileCount:34, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { scenario: "The longest strings this card must survive",
      title: "For All Mankind", posterUrl: "/api/metadata/artwork/fee1f7759a1db260be24c2961b3687ebc604e7e41831b4ec01a11b4af6eb1ce3",
      wantedStatus:"missing", monitored:true, airedEpisodeCount:170, airedWithFileCount:148, subtitleLanguagesWanted:12, subtitleLanguagesHeld:900 }
  ]
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
    document.getElementById("copy").onclick = () => {
      navigator.clipboard.writeText(medium.toUpperCase() + " — " + settingsLine() + " — " + location.href)
        .then(() => { const b = document.getElementById("copy"); b.textContent = "Copied";
                      setTimeout(() => { b.textContent = "Copy"; }, 1400); }, () => {});
    };
  }

  function drawPresets() {
    const el = document.getElementById("presets");
    if (!el) return;
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
      + MARKS[k] + '</span>').join("") + '</div>';
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
      + '<h2 class="mx">Every scenario, once <small>one card per distinct state a '
      + noun + ' can be in — no repeats, and every toggle both ways</small></h2>'
      + wallHtml(SCENARIOS[medium]);

    if (DATA.items.length) {
      html += '<h2 class="mx">Your library <small>' + DATA.items.length + ' '
        + noun + (DATA.items.length === 1 ? '' : 's') + ', as they actually are</small></h2>'
        + wallHtml(DATA.items);
    }
    document.getElementById("shelf").innerHTML = html;
  }

  function drawAll() {
    document.body.className = S.theme === "light" ? "light" : "";
    drawRail(); drawPresets(); drawBanner(); drawShelf();
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
      const res = await fetch(path + "?pageSize=60&sort=title&direction=asc",
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
  window.__decider = { S, CONTROLS, surfaces, nearestTo, whiteOn, deltaE, medium };
}
