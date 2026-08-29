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
  labels: "subs",
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
    { key: "labels", label: "Labels", opts: [["none","None"],["subs","SUBS only"],["both","Both"]] },
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
  return { held: Math.min(held, wanted), wanted };
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
  return { pct: 0, label: MARKS[mark], lead: "QLTY", fraction: mark === "miss" };
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

function cardHtml(it, isShow) {
  const C = surfaces(), mark = markFor(it);
  const media = mediaBar(it, isShow);
  const subs = subtitleBar(it, isShow);
  const subPct = subs.wanted ? Math.round(subs.held / subs.wanted * 100) : 0;
  /* "None asked for" is not a state: subtitles Subber has not resolved are
     simply not here, and not here is Missing. No denominator to print, so the
     bar prints the word — as a film's media bar does with no quality to name. */
  const subLabel = subs.wanted ? subs.held + " / " + subs.wanted : MARKS.miss;

  const T = track(mark), TS = track("miss");

  /* A bar with no fraction keeps its state's colour under either grammar: an
     Upcoming title has not started, a downloading one has no held part yet. */
  const topFill = media.fraction ? fillColourFor(mark, media.pct === 100, C) : "hsl(" + C[mark] + ")";
  const topOnFill = topFill === "hsl(" + C.gold + ")" ? labelOn("gold") : "hsl(0 0% 100%)";

  const subSettled = subs.wanted > 0 && subs.held === subs.wanted;
  const subFill = subSettled ? "hsl(" + C.gold + ")" : "hsl(" + C.upg + ")";
  const subOnFill = subSettled ? labelOn("gold") : labelOn("upg");

  const showText = S.labels !== "none";
  const mediaLead = S.labels === "both" ? media.lead : null;
  const subLead = S.labels === "none" ? null : "SUBS";

  const thin = (pct, colour, t) => '<div class="bar" style="height:5px;background:' + t.colour
    + '"><div class="fill" style="width:' + pct + '%;background:' + colour + '"></div></div>';

  const topBar = showText
    ? twoTone(topFill, media.pct, media.label, mediaLead, topOnFill, T.colour, T.label)
    : thin(media.pct, topFill, T);
  const botBar = showText
    ? twoTone(subFill, subPct, subLabel, subLead, subOnFill, TS.colour, TS.label)
    : thin(subPct, subFill, TS);

  const art = '<div class="art">'
    + (it.posterUrl ? '<img loading="lazy" src="' + esc(it.posterUrl) + '" alt="">'
                    : '<div class="noart">no art</div>')
    + '<div class="name">' + esc(it.title) + '</div></div>';

  /* No corner pill, and the bars are always on the artwork — both settled:
     "corner pill is a complete removal and bars always on artwork". */
  return '<div class="card">' + topBar + art + botBar + '</div>';
}

/* ══════════════════════════════════════════════════════════════
   Sample data, used only until the page is opened from a signed-in tab
   ══════════════════════════════════════════════════════════════ */
const SAMPLE = {
  tv: [
    { title:"Severance", wantedStatus:"missing", airedEpisodeCount:20, airedWithFileCount:3, subtitleLanguagesWanted:2, subtitleLanguagesHeld:2 },
    { title:"Shōgun", wantedStatus:"covered", airedEpisodeCount:10, airedWithFileCount:10, subtitleLanguagesWanted:2, subtitleLanguagesHeld:20 },
    { title:"Foundation", wantedStatus:"missing", airedEpisodeCount:29, airedWithFileCount:0, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { title:"Dune: Prophecy", wantedStatus:"upcoming", airedEpisodeCount:0, airedWithFileCount:0, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 },
    { title:"Silo", wantedStatus:"airing", airedEpisodeCount:10, airedWithFileCount:10, subtitleLanguagesWanted:2, subtitleLanguagesHeld:14 },
    { title:"Slow Horses", wantedStatus:"upgrade", airedEpisodeCount:6, airedWithFileCount:6, subtitleLanguagesWanted:1, subtitleLanguagesHeld:4 }
  ],
  movies: [
    { title:"Dune: Part Two", wantedStatus:"covered", hasFile:true, currentQuality:"Remux-2160p", subtitleLanguagesWanted:3, subtitleLanguagesHeld:3 },
    { title:"The Substance", wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBDL-1080p", subtitleLanguagesWanted:3, subtitleLanguagesHeld:1 },
    { title:"Nosferatu", wantedStatus:"downloading", hasFile:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:0 },
    { title:"Conclave", wantedStatus:"missing", hasFile:false, subtitleLanguagesWanted:3, subtitleLanguagesHeld:0 },
    { title:"Anora", wantedStatus:"upcoming", hasFile:false, subtitleLanguagesWanted:0, subtitleLanguagesHeld:0 },
    { title:"Sinners", wantedStatus:"covered", hasFile:true, currentQuality:"Bluray-1080p", subtitleLanguagesWanted:2, subtitleLanguagesHeld:2 },
    { title:"The Brutalist", wantedStatus:"upgrade", hasFile:true, currentQuality:"WEBRip-720p", subtitleLanguagesWanted:2, subtitleLanguagesHeld:2 },
    { title:"Wicked", wantedStatus:"missing", hasFile:false, subtitleLanguagesWanted:2, subtitleLanguagesHeld:0 }
  ]
};

/* ══════════════════════════════════════════════════════════════
   Mount
   ══════════════════════════════════════════════════════════════ */
const WIDTHS = { sm: 108, md: 148, lg: 190 };

function mountDecider({ medium }) {
  const isShow = medium === "tv";
  const CONTROLS = controlsFor(medium);
  let DATA = { items: SAMPLE[medium], live: false, reason: "" };

  try {
    const h = new URLSearchParams(location.hash.slice(1));
    for (const k of Object.keys(S)) if (h.get(k)) S[k] = h.get(k);
  } catch (e) { /* leave the defaults */ }

  function settingsLine() {
    return CONTROLS.filter(c => c.key !== "theme" && c.key !== "size")
      .map(c => c.label + ": " + (c.opts.find(o => o[0] === S[c.key]) || ["", "?"])[1])
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
      el.innerHTML = "Drawing your real library — <b>" + DATA.items.length + " " + noun
        + "</b>, with their own artwork and counts.";
      return;
    }
    el.className = "banner warn";
    const why = DATA.reason === "notab" ? "This tab is not signed in."
      : DATA.reason === "empty" ? "The library came back empty."
      : "The library could not be read (" + esc(DATA.reason) + ").";
    el.innerHTML = "<b>Showing sample cards.</b> " + why
      + " To draw your own: from your signed-in Deluno tab, paste this page's address into"
      + " <b>that same tab</b> and press enter. The session token lives in that tab only.";
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

  function drawShelf() {
    document.getElementById("shelf").innerHTML =
      clearanceHtml() + legendHtml()
      + '<div class="wall" style="grid-template-columns: repeat(auto-fill, ' + WIDTHS[S.size] + 'px);">'
      + DATA.items.map(it => cardHtml(it, isShow)).join("") + '</div>';
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
