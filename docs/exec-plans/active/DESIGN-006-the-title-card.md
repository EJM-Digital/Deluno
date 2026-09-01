# DESIGN-006 — The title card, per medium

> **Films: built and on the rig.** Settled by James as *Depth: Deep · Quality on
> bar: On · Subtitle count: On · Lead words: SUBS only · Track: Missing red*.
>
> **Shows: built and on the rig.** Approved as *Depth: Deep · Episode count on
> bar: On · Subtitle count: On · Lead words: SUBS only · Track: Missing red ·
> Fill: held green, Quality met gold when complete · Unmonitored: one flat grey
> override*. TV keeps its own episode-coverage declaration and adopts the shared
> mechanism independently of Movies.

Settled 2026-08-30 with James. Supersedes the card half of
[DESIGN-001](DESIGN-001-title-marks.md) and the poster half of the Option A
decision in `4bdfe45`. The subtitle vocabulary from
[DESIGN-002](DESIGN-002-subber.md) is unchanged and inherited.

Rendered references on the rig:

- **`/renders/card-decider-movies.html`** — the film card, every open decision as a
  switch, drawn on the real library. **Start here.**
- **`/renders/card-decider-tv.html`** — the show card, same.

  Each page draws **every scenario once** — one card per distinct state, no repeats,
  every toggle both ways — above the live library. The catalogue is drawn whether or
  not the page is signed in, because a real library cannot be relied on to contain a
  downloading title or an unmonitored one at the moment you happen to look, and those
  are exactly the cards a design fails on. Each card carries its status, what that
  status means, and why its bar is the length it is.

  One page per shelf, because one page carrying both was too hard to read — James:
  *"we need to split this up I think, its too confusing for me and everyone... lets
  focus on Movies first"*. Splitting it proved his point immediately: **Continuing
  does not exist on the movie shelf**, so a third of the questions on that page were
  noise. The movie page has no Continuing switch, no Continuing legend rung and no
  clearance table.

  Two pages, one implementation: `ui-explorations/card-decider-core.js` draws the
  card and each page mounts it with its medium. The pages differ only in the prose
  they carry.
- `/renders/bars-that-speak.html` — the treatments side by side, with the reasoning
  written between them. (source: `ui-explorations/bars-that-speak.html`)

James: *"there is a 'show text on progress bar' for both sonarr and radarr —
sonarr looks to display episode count 0/0 whereas radarr shows quality — this is
what we need to properly mimic but with a better name tailed to movies and tv"*,
and *"I feel like we need to be more explicit about top being about media and
bottom bar being subtitle"*.

---

## 1. The correction this document exists to make

`4bdfe45` removed the label from the state bar and concluded:

> a word whose ground changes with the episode count cannot be given a colour
> that works on every card

**That conclusion is wrong**, and it cost the card its most useful fact. Sonarr
and Radarr both print a label on exactly such a bar and neither washes out. Their
answer is not a better colour. It is that **the label is drawn twice**:

| Layer | Extent | Colour |
|---|---|---|
| Back text | the whole bar | chosen for the **remainder** — white, since the remainder is Missing red |
| Fill | 0–N% of the bar | the mark's surface colour |
| Front text | clipped to exactly N% | chosen for the fill |

Both text layers are the **same string in the same position**. Only the paint of
the front one is clipped. A bar that is 15% full therefore has 15% of its label
in the fill's label colour and 85% in the remainder's, and every glyph is coloured for the ground directly
beneath it. Verified against the live Sonarr at `192.168.1.35:8989` — three DOM
layers, `ProgressBar-backTextContainer` / `progressBar` / `frontTextContainer`.

**Two implementation notes, both of which cost a round to find.**

**Clip the front layer, do not size it.** `clip-path: inset(0 <100-N>% 0 0)` over an
identically positioned element. Making the front container `width: N%` and centring
the text inside it centres the label on the *fill* instead of the *bar*, so it slides
sideways as the bar fills — visibly wrong at 3 of 20.

**Clip the back layer to the complement, `inset(0 0 0 N%)`.** Leaving it unclipped is
the more insidious bug, because on a *fully filled* bar both layers then paint the
identical glyphs on top of each other. Every antialiased edge pixel is composited
twice, so the semi-transparent edges turn opaque and the text thickens and glows —
literally a double exposure, and worst on saturated grounds. James, twice: *"the
quality text is still a little hard to read on small posters almost like its
overexposed"*, then *"the font on green deep still looks so overexposed to me is
there something wrong here?"* There was, and it was this.

Clipped both ways, each glyph region is painted exactly once and the two halves tile
at the fill edge with no overlap and no seam. Asserted in the audit.

---

## 2. A film and a show are different questions

This is the spine of the document, and it is not a preference. It follows from
what the two media are:

**A film is one file.** The reader's question is *do I have it, and is it any
good?* — and the answer to the second half is a single value, because there is a
single file to describe.

**A show is many files.** The reader's question is *how far through am I, and is
more coming?* A show has no single quality: twenty episodes can sit at twenty
different tiers, and printing any one of them on the card would be picking an
arbitrary file and calling it the show. That is the exact defect
`status-tones.ts` records as the reason a show's rung moved to the server.

So the top bar cannot say the same thing on both shelves, and the split Sonarr
and Radarr arrived at is the correct one:

| | Film | Show |
|---|---|---|
| **Top bar says** | the quality on disk — `Bluray-1080p` | aired episodes held — `3 / 20` |
| **Top bar fills to** | download progress, or solid when held | the fraction of aired episodes held |
| **When nothing is held** | the state's word — `Missing`, `Upcoming` | `0 / 29`, with the Missing-red remainder across an empty monitored bar |
| **Bottom bar says** | `SUBS 1 / 3` | `SUBS 2 / 6` |
| **Bottom bar counts over** | its one file | the episodes you actually hold |

### Why the fill means two different things

For a **show** the fill is coverage: how much of what has aired is on disk. For a
**film** there is nothing to be partway through — you have it or you do not — so
the fill is free to mean **download progress**, which is the one time a film is
genuinely part-way. A held film is a solid bar; a downloading film fills as the
bytes arrive; a missing film is a bar with no fill at all.

This is not an inconsistency to apologise for. Each medium's bar fills with the
only fraction that medium has.

### What the unfilled part of a bar is

**Missing red.** The remainder is the part Deluno still needs to acquire, so it
uses the same Missing surface as the movie bar and the subtitle bar. This is the
approved composition rule for TV, not a faded state colour and not a neutral
placeholder.

The filled part is deliberately different so a partial Missing show remains
readable: **green means the aired episode is held**, while **gold means every
aired episode is held at the requested quality**. The length remains the fraction
of aired episodes held. For example:

| Series state | Track/remainder | Held fill | Fill width |
|---|---|---|---|
| Continuing, all held | Missing red | green | 100% |
| Quality met, all held | Missing red | gold | 100% |
| Missing episodes | Missing red | green | 15% for `3 / 20` |
| Missing, none held | Missing red | no visible fill | 0% |

There is no monitored empty-bar exception: the red remainder is already visible
at `0 / 29`. The only grey is the explicit **Unmonitored override**, which paints
both fill and remainder one flat grey and takes priority over every lifecycle or
coverage colour.

### The subtitle bar counts only files you hold

Unchanged from DESIGN-002 and restated because it is easy to lose: a show short
of episodes is already saying so on its top bar, in red, on the same card.
Dragging the subtitle bar down for the same reason would be the same fact twice.

### "None asked for" is not a state

A title whose `subtitleLanguagesWanted` is zero was drawn as a grey bar reading
*none asked for*, which invented a fourth thing a subtitle bar can be. James:
*"this is an interim state right... in the event the subtitle is not available it
would go to missing so my gut feeling is it should just be missing regardless
cause it is missing"*.

He is right, and it is the same error as the neutral remainder. Zero-wanted is
where Subber has not resolved the title yet — the true state today of every title
in the library, since #301 has not landed — and a title whose subtitles are not
here is Missing. There is no denominator to print, so the bar prints the **word**,
exactly as a film's media bar does when it has no quality to name.

Deluno must never show a subtitle bar that means "nothing to say". A bar that can
be a fourth, verdict-free thing is a bar a reader has to learn a fourth rule for.

### And Missing is not the only way to have nothing

James: *"upcoming is the wrong colour, its missing yes but its upcoming too"*.

The rule above said *not here is Missing*, and applied it to every title. That is
wrong, and it threw away the one distinction the ladder exists to make: **Missing
means it is out and you do not have it.** An Upcoming title is not out. You cannot
be missing a subtitle for a file that cannot exist yet.

So when a title holds no files, the subtitle bar inherits — but **only Upcoming**.
That first read as "inherit whatever the title's reason is", which over-corrected;
James: *"subs should not be downloading it should always be missing"*.

He is right, and the reason is sharper than the one he gave. **Upcoming and
Downloading are different in kind.** Upcoming means the thing *cannot exist yet* —
not released, not aired — so nothing can be fetched and calling it Missing is a
category error. Downloading means it exists and is arriving, so its subtitles exist
out there and you do not have them, which is precisely what Missing means.

His own argument is the second reason and stands on its own: a film's download earns
a progress bar because it lasts, while a subtitle is a few kilobytes of text and
would be here and gone before the bar could be read. *"seeing a progress bar for subs
will be here and gone in seconds"*. **A state nobody can ever see should not be
modelled** — the same rule that keeps a filter chip which can never match off the
shelf.

| Title | Its subtitle bar reads |
|---|---|
| Upcoming | `SUBS Upcoming`, in violet — the one inheritance |
| Downloading | `SUBS Missing`, in red |
| Missing | `SUBS Missing`, in red |
| Held, languages short | `SUBS 1 / 3` — a real count |
| **Not monitored** | grey, whatever the above says |

Once there **is** a file the bar is about subtitles again, and a language Subber has
not found for a file you hold is genuinely missing.

The same correction applies to the media bar: an Upcoming **film** was drawing a 0%
bar while an Upcoming **show** drew a solid one, so the two shelves disagreed about
the same state. Neither is partway through anything, so both draw solid. Only a
Missing film is genuinely an empty bar.

---

## 3. Anatomy

```
┌───────────────────────────┐
│ ▓▓▓█████ 3 / 20 █████████ │  ← media bar, 14px  ▓ held  █ missing
│                           │
│         artwork           │
│                           │
│  Severance                │
│ ▓▓▓▓▓ SUBS 2 / 6 ████████ │  ← subtitle bar, 14px  ▓ held  █ missing
└───────────────────────────┘
```

Both bars are **16px**, full bleed, on the card's top and bottom edges, **always on
the artwork** — settled by James: *"corner pill is a complete removal and bars always
on artwork"*.

**The label is 11.5px at weight 700**, not 10px at 900. Two separate faults: at 10px
`WEBDL-1080p` was too small even where contrast passed — Sonarr and Radarr both set
12px on a 15px bar — and the `<b>` inside a 700 parent resolved to **900**, which is
what made white on a saturated bar bloom. James: *"almost like its overexposed"*. The
tag is there for structure, so it is pinned back to 700.

**And the label is a hard argument against the Shipped palette.** White on the
shipped dark green is **1.69:1** — not marginal, illegible. Deep gives 5.49 and Jewel
7.36. A ladder that cannot carry a white label is a ladder that cannot carry this
design, so Shipped is out on measurement rather than taste.

### How a reader knows which is which

Three things say it, in the order they are noticed:

1. **The bottom bar leads with the word `SUBS`. The top bar does not lead with
   anything.** The asymmetry is the signal: one bar names its subject, the other
   is obviously about the title itself. This is the whole of James's *"be more
   explicit about top being about media and bottom bar being subtitle"*, and it
   costs eight pixels on one bar rather than a label on both.
2. **Position is stable.** Media is always the top edge, subtitles always the
   bottom. Neither ever swaps, on either shelf, at any card size.
3. **The content is unmistakable once read.** `3 / 20` and `Bluray-1080p` are
   not things a subtitle count could say.

`EPS` and `QLTY` leads on the top bar were drawn and rejected: on a 148px card
the lead and the value compete for width, and the top bar's content already
identifies itself. Only the ambiguous bar gets a label.

---

## 4. Colour: two jobs, two tokens

James: *"more obvious change with deeper proper colours"*.

The rungs go deep. But **the same token cannot be both the bar surface and the
chip's text**, and this is where deepening would have broken things silently:
measured on the running build, the deep values used as text on the dark card give
Missing 3.20:1, Downloading 3.37:1 and Upcoming 3.11:1 — all below AA for the
small bold text the chip counts use.

Gold already had this exact problem and already had the answer:
`TitleMarkPresentation.surfaceVar`, added because gold's semantic value is a
*text* colour and is dark in the light theme, and a dark yellow painted on a
poster is brown. **That exception becomes the rule.** Every rung gets both.

### Surface — what a bar is painted with

**One set for both themes.** A bar sits on artwork, not on the page, so there was
never a reason for it to invert — the same argument that already makes
`--mark-leaf-*` theme-independent.

| Rung | Surface | Label on it | Contrast |
|---|---|---|---|
| Missing | `hsl(356 84% 41%)` | white | 6.27 |
| Downloading | `hsl(214 94% 40%)` | white | 6.44 |
| Upgradable | `hsl(150 90% 25%)` | white | 5.49 |
| Continuing | `hsl(318 78% 38%)` | white | 6.60 |
| Quality met | the existing `--mark-leaf-*` gradient | **near-black** `hsl(40 90% 12%)` | 10.46 |
| Upcoming | `hsl(270 76% 47%)` | white | 7.22 |
| Remainder (track) | Missing red `hsl(356 84% 41%)` | white | 6.27 |
| Unmonitored override | `--mark-unmonitored` | white | — |

**Gold's label is the one asymmetry, and it is forced.** Gold is floored at 52%
lightness — below that yellow reads as bronze, which is the whole of
`gold-stays-gold.test.ts` — so white can never sit on it. Every other bar takes
white; gold takes near-black. Do not "fix" this by darkening gold.

### Text — what a count or a word is coloured with

Per theme, the matching movie values stay unchanged; Continuing now uses the
approved magenta hue on the page as well as on the TV card.

| Rung | Light | Dark |
|---|---|---|
| Missing | `hsl(0 84% 48%)` | `hsl(0 84% 62%)` |
| Downloading | `hsl(207 92% 45%)` | `hsl(207 96% 62%)` |
| Upgradable | `hsl(145 72% 34%)` | `hsl(145 78% 52%)` |
| Continuing | `hsl(318 74% 30%)` | `hsl(318 76% 50%)` |
| Quality met | `hsl(42 96% 40%)` | `hsl(44 98% 58%)` |
| Upcoming | `hsl(268 62% 50%)` | `hsl(268 82% 72%)` |

### The wheel is not full — Continuing goes magenta

James: *"I really wish we could find a better colour for continuing... have we
exhausted all the standard colours already? red and yellow and pink and green
purple and orange and blue"*.

We had not. Five rungs are fixed — Missing red, Downloading blue (also the app's
primary), Upgradable green, Quality met gold, Upcoming violet — and amber is
reserved app-wide for *a person is needed* even though it never appears on a title.
Sweeping the whole hue circle against those six, **the roomiest arc left is magenta
and pink, and nothing in Deluno uses it.**

Teal was never the last free colour. It is a *crowded* one, wedged 28° from green
and 36° from blue, which is why moving it 184 → 178 bought so little. Measured at
the tuned values each candidate would actually ship at:

| Candidate | Clear by | of its nearest rung | White label |
|---|---|---|---|
| **Magenta `318 78% 38%`** | **ΔE 54.7** | Upcoming | 6.60 |
| Steel `200 26% 40%` | ΔE 51.1 | Upgradable | 5.41 |
| Pink `330 78% 40%` | ΔE 46.2 | Missing | 6.39 |
| Teal `178 96% 24%` | ΔE 32.6 | Upgradable | 5.33 |
| Cyan `192 95% 27%` | ΔE 32.5 | Upgradable | 6.00 |
| Lime `92 88% 24%` | ΔE 23.6 | Upgradable | 5.78 |

Magenta clears its nearest neighbour by **two-thirds more than teal does**, and it is
the only candidate that puts Continuing in an arc nothing else occupies. Lime — the
other free arc, between gold and green — is the worst of the six; the gap looks open
on a colour wheel and is not one in Lab.

The candidates are switches on the decider page, each printing its own clearance,
computed rather than written down so a value cannot be edited without its number
moving with it.

### Continuing no longer collides with Downloading's blue

The pair James flagged was Continuing and Upcoming. Measured, those two are the
*furthest apart* on the ladder (ΔE 88). The pair that actually collides is
**Continuing and Downloading** — ΔE 49, hues 184 and 207, the smallest gap of any
two rungs and both a bright cool blue. He confirmed that is the one he was
looking at.

Continuing moves from the crowded cool-blue/teal area to Magenta 318. Downloading
does not move: it is the app's primary.

| Pair | Before | After |
|---|---|---|
| Downloading vs Continuing, surface | ΔE 49 | **ΔE 60.3** |
| Downloading vs Continuing, text (dark) | ΔE 49 | **ΔE 85.9** |
| Upgradable vs Continuing, surface | — | **ΔE 115.7** |

Upgradable vs Continuing at ΔE 33 is the new nearest pair and is comfortably
clear; it is recorded here so the next person moving a hue knows which neighbour
they are moving toward.

### Pre-existing, not fixed here

Several **light-theme text** tokens already fall short of AA on white and this
document does not change them: Downloading 4.20, Upgradable 3.85, Quality met
2.89. Gold's is deliberate — it is the dark "Quality met" count. The others
predate this work. **The deep surfaces do not make any of them
worse**, and fixing them is its own issue, not a silent rider on this one.

---

## 5. Monitoring — the bars go neutral, the shield stays underneath

Four attempts, three of them mine and each wrong for a different reason. The road is
kept because the reasons are the useful part.

1. **Halving the bar's fill.** DESIGN-001 gives an unmonitored title a half-grey
   *dot*; this was carried straight over to the *bar*. James: *"I think the half was
   in reference to the dots which we have removed"*. Right — and the render proved it
   before the argument did: on a Missing title, whose fill is 0% wide, the half
   rendered as **nothing at all**. A half works on a dot because a dot has no length
   of its own. A bar *is* a length, and that length already means the fraction held.

2. **Overriding the bars with a flat neutral.** Worked. I then talked myself out of
   it, on the grounds that it "spends the bar's colour on a fact that is not about
   the title's state" — which is backwards. An unmonitored title's rung is not
   telling you to do anything, so its colour is the thing *least* worth keeping.

3. **A shield badge in the corner of the artwork.** Reads well, and it is one more
   thing on the picture for a fact Deluno already says in words on a line under the
   poster, behind its own switch.

4. **A neutral bar, with the shield kept as a line underneath.** Nearly right, and I
   misread the last clause: *"kept under in the selectable options"* meant kept in
   the option list, not kept as a line under the poster.

5. **The line goes too.** James: *"why is monitored under the poster, it doesnt need
   to be there anymore"*. Right — and the reason is one this codebase has already acted on
   twice: **when a bar starts saying a fact, the switch that used to say it is
   removed.** `4bdfe45` deleted the Quality poster option the moment the bar carried
   the quality, and `showEpisodeProgress` went the same way. A line reading *Not
   monitored* under a card whose bars have already gone neutral is the same fact
   twice.

6. **Settled, including the colour.** *"unmonitored titles are the override, they
   are always grey — once they are monitored they inherit the normal statuses"*.

### The rule

**Unmonitored is an override, and it is always grey.** James: *"unmonitored titles
are the override, they are always grey — once they are monitored they inherit the
normal statuses"*.

One rule, no switch. If Deluno is not watching a title, both its bars are
`hsl(220 8% 46%)` whatever rung it sits on — because that rung is not telling you to
do anything. The moment it is monitored again it inherits the ladder like any other
title: nothing is remembered, nothing is special-cased.

| | Monitored | Not monitored |
|---|---|---|
| Both bars, **fill and track alike** | the state's colour | **one flat grey `hsl(220 8% 46%)`** |
| Anywhere else on the card | nothing | nothing |

**Fill and track are the same value, and that is not a detail.** The override was
first applied to them separately, which produced *two* greys: a Missing film has a
0%-wide fill so you saw the track, while a fully-held one showed the fill — and which
grey you got depended on the title's rung, the very thing an override exists to stop
mattering. James: *"why is the martian different grey to mad max and big buck bunny
come on."* An unmonitored card is one flat grey bar, identical everywhere. The count
or quality is still written on it, so the number survives; only the colour goes.

**And grey therefore means exactly one thing on each shelf.** Movies retain the
**Missing red** track, with held green and Quality met gold. TV now uses the same
composition for its episode-coverage bar: held green, Quality met gold, and
Missing red for the remainder. The unmonitored override replaces both with
`--mark-unmonitored`, so grey is never a monitored coverage state.

**This is the only override in the design.** Everywhere else colour is decided by the
title's state; here the state is overruled outright. On the shelf that reads as: two
titles both *Missing*, one red and one grey, and the grey one is the one Deluno will
not act on.

White sits on the grey at 4.82:1.

A card is now exactly three things: a bar, the artwork, a bar.

**`showMonitored` is deleted from `CatalogueControls.cs`**, alongside the Quality and
Episode-progress options that went for the same reason. Monitoring is not switchable
on a card: it is a fact about whether Deluno will act at all, and a reader turning
off a display line should not be able to hide that from the shelf.

`library-grid.tsx` loses the shield line, and `ShieldCheck`/`ShieldOff` may then have
no remaining use in the grid — check before removing the imports.

The **detail pages keep their shield**, as the pressable control settled below. That
is a different job: on a card you are reading the fact, on a detail page you are
changing it.

**Black or grey is open.** The thing to watch is that the track is already grey, so a
grey fill on a grey track could make the bar vanish and take the fraction with it.
Measured, both stay clear — black ΔE 19.2 dark / 74.9 light, grey 21.3 / 35.5 — so
it is a taste call, not a legibility one. Both are switches on the decider pages.

**`canBeHalf`** in `TITLE_MARK_PRESENTATION` now has no consumer on the card. Do not
delete it blindly — the drawer and detail page may still read it — but check, because
a flag nothing reads is the shape of the defects this project keeps finding.

### The control, and the words — settled### The control, and the words — settled

Chosen from four rendered variants (`/renders/monitoring-control.html`), James:
*"honestly I like A"*. **An icon whose tooltip carries the state.** It is Sonarr's
exact shape, and the cheapest in space — which is what matters, because this control
has to appear at four levels and one of them is an episode row.

**The state is not hidden by it.** The glyph differs — shield against slashed shield
— so the fact reads without hover; what hover adds is the *word* and the action. An
earlier note in the render claimed the state was invisible until hovered, and that
was wrong.

**A state word describes, a verb instructs.** They are different jobs, not competing
options, so both are kept and each is used where it belongs:

| Where | Reads |
|---|---|
| Card | said by the bars going neutral — no words |
| Movie header, series header, season row, episode row | `Monitored — click to unmonitor` · `Not monitored — click to monitor` |

The verb pair is fixed: **Monitor** and **Unmonitor** are what pressing does and
nothing else says it as briefly. The state pair is **Monitored / Not monitored**,
which is already the majority in the app — the poster line, the overview and the
filter panel all use it.

### Monitoring is also *selectable*, in three places

James: *"we do have to remember we also have monitored / unmonitored as a
selectable option as well"*. Three, and they are not the same kind of thing — one
filters, one displays, one edits:

| Selectable | What it is | Says today | Verdict |
|---|---|---|---|
| **Filter** — `MonitoringFilter` | narrows the shelf | `Any monitoring` · `Monitored (n)` · `Not monitored (n)` | **already correct**, leave it |
| **Poster option** — `showMonitored` | showed the shield line under the poster | switch labelled `Monitoring` | **deleted** — the bars say it |
| **Bulk edit** | sets the fact on a selection | `Monitored` / `Unmonitored` | → `Not monitored` |

The internal value `"unmonitored"` stays as it is; it is a code identifier and never
reaches a reader.

#### `showMonitored` is deleted

Two earlier revisions of this document had it controlling a corner badge, then a
line. It controls neither: the bars say monitoring now, so the option goes the way
of Quality and Episode progress. See §5.

### The seven vocabularies this replaces

One fact currently has seven wordings, none able to check the others. This is the
same defect shape as everything else in this document, in words rather than colour,
and adopting the above means **all of these change**:

| Where | Says today | Should say |
|---|---|---|
| Poster line | `Monitored` / `Not monitored` | **gone** — the bars say it |
| Overview row | `Monitored` / `Not monitored` | unchanged |
| Filter panel | `Not monitored (n)` | unchanged |
| Bulk dialog dropdown | `Monitored` / `Unmonitored` | `Monitored` / `Not monitored` |
| Movie detail stat row | `On` / `Paused` | `Monitored` / `Not monitored` |
| Movie detail eyebrow | `Monitoring paused` | `Not monitored` |
| Movie detail button | `Monitor` / `Stop monitoring` | the icon control above |
| Bulk operation dropdown | `Monitor or unmonitor` | `Monitor or unmonitor` (a verb pair — correct) |
| Selection command bar | `Monitor` / `Unmonitor` | unchanged — verbs on buttons |

### Where the control has to appear

James: *"whatever we pick needs to go into the title detail as well so if you are in
a movie or series / season you can just toggle it on or off"*.

| Level | Endpoint |
|---|---|
| Movie | `PUT /api/movies/monitoring` — exists |
| Series | `PUT /api/series/monitoring` — exists |
| Episode | `PUT /api/series/episodes/monitoring` — exists |
| **Season** | **none** — sets its episodes in bulk through the episode endpoint, which is how Sonarr does it |

Only the season level is new work, and it needs no new endpoint.

`canBeHalf` in `TITLE_MARK_PRESENTATION` therefore has no consumer on the card any
more. Do not delete it blindly — the drawer and detail page may still read it — but
check, because a flag nothing reads is the shape of the defects this project keeps
finding.

## 5a. What actually sits on the card

**Only the two bars and the monitoring badge.** Nothing else.

The title was first drawn over the picture behind a gradient — which is not what the
shelf does. James: *"we shouldnt have the title on the poster we dont even have that
now, its a selectable option that appears under the poster so its not a true
representation"*. Moving it to a line beneath the artwork was still wrong for a page
whose job is to decide the bars: *"still wrong take them out entirely please for this
exercise its not needed as we mentioned its a switchable line"*.

He is right, and the principle generalises: **a render that carries switchable
furniture which is not part of the decision is not a neutral render.** It adds
height, competes for attention, and invites judging the card on something nobody is
choosing. The decider pages carry the title in the description underneath, where it
identifies the card without being part of it.

`showTitle` remains exactly what it is in the app — a poster option, on by default,
rendered as a line under the artwork. This document does not change it.

## 6. The switch

Sonarr and Radarr both call it **"Detailed Progress Bar — show text on progress
bar"**, which names the widget and says nothing about what appears. Deluno's is
named for what it will actually put on the card, and therefore **differs by
shelf** — which the architecture already supports: `CatalogueControls.For(kind)`
composes `SharedPosterOptions` with `MovieOnlyPosterOptions` or
`SeriesOnlyPosterOptions`.

| Shelf | Id | Label | Description | Default |
|---|---|---|---|---|
| Movies | `showQualityOnBar` | **Quality on the bar** | The quality you hold, written across the top bar | on |
| TV | `showEpisodeCountOnBar` | **Episode count on the bar** | How many aired episodes you hold, written across the top bar | on |
| Both | `showSubtitleCountOnBar` | **Subtitle count on the bar** | How many of the languages you asked for are here | on |

Three switches rather than one, because they are three independent facts and a
reader who wants the episode count but not the subtitle count should get it. It
also keeps the rule the option list already follows — one switch, one fact.

**Bars are never switched off, only their words.** With a switch off its bar falls
back to the 5px strip Deluno ships today — it keeps saying the state *and* the
fraction, it just stops spelling them out. **Turning a switch off must never remove
a fact that has nowhere else to live**, and it does not: colour and length both
survive. The state mark stays mandatory, per James on DESIGN-001.

These are user switches, not design decisions, so **all four combinations are cards
a real person will see** — James: *"some of these options are selectable for on and
off so what are we doing about that here"*. Both decider pages draw the four
together, above the shelf, rather than leaving an off-state to be found by flipping;
an off-state left unlooked-at is where a design that leans on its label falls over.

---

## 7. What leaves the card

**The corner pill goes.** It exists to carry the count for a show and the state's
word for a film, and the top bar now says both — on the same card, in the same
place, for both media. Keeping it would be the same fact twice, which is the
reason `showEpisodeProgress` was removed as a switch in `4bdfe45`.

**Quality returns**, on the movie shelf only, on the bar. It was removed as a
poster option on the reasoning quoted in §1, and that reasoning was wrong.
`CatalogueControls.cs` carries a comment block asserting it; that comment must be
replaced, not left to contradict the code.

---

## 8. What is never on the card

Unchanged from DESIGN-001 and repeated because it is what keeps the shelf
readable: no failures, no machinery health, nothing blocked on a person. Those
live in Transfers, Activity and Needs You. That is what frees red for *Missing*
and keeps amber meaning "a person is needed" — amber never appears on a title.

---

## 9. Accessibility

- Each bar is one `role="img"` with an `aria-label` that reads as a sentence:
  `"Continuing · 3 of 20 aired episodes on disk"`,
  `"Quality met · Bluray-1080p"`, `"2 of 6 subtitle languages"`.
- The two text layers are **`aria-hidden`**. They are one string rendered twice
  and a screen reader must not hear it twice.
- The label is never the only carrier of the state: the fill colour and the
  `aria-label` both say it, so the switch being off costs a sighted reader
  detail and costs a screen-reader user nothing.
- Contrast: every surface/label pair in §4 is at or above 5.3:1.

---

## 10. What this changes in code

| File | Change |
|---|---|
| `apps/web/src/index.css` | Six `--mark-*-surface` tokens, theme-independent. TV-only Continuing uses the approved magenta hue 318 in both themes; matching movie states keep their existing palette. Gold untouched. |
| `tailwind.config.*` | The surface tokens spelled out as literals, like every other mark colour. |
| `lib/status-tones.ts` | `surfaceVar` becomes required, not optional, on `TitleMarkPresentation`. Add `labelOnSurface` — every rung white except gold. |
| `components/ui/title-mark.tsx` | `TitleMarkTopBar` grows to 14px and takes the two-tone label. New `TitleMarkSubtitleBar` on the same primitive. `TitleMarkCorner` deleted. |
| `Deluno.Contracts/CatalogueControls.cs` | Three switches per §6. Replace the "Quality is not a poster option any more" comment. |
| `components/app/library-grid.tsx` | Card assembles two bars; corner removed. |

The two bars must be **one primitive with two configurations**, not two
components. They differ only in what they are given, and this codebase's whole
defect history is one rule written twice in two places that cannot check each
other.

---

## 11. Tests, and what each must prove

Every one of these has to be watched failing before it is trusted — break the
fix, see it go red, restore it.

1. **The label is drawn twice, in the same place.** Assert both layers carry the
   identical string and identical offset, and that only `clip-path` differs.
   Break it by centring the front layer and the test must fail.
2. **Every surface carries its label at ≥4.5:1.** Computed from the tokens, not
   asserted as a literal, so a future hue change cannot pass by editing the
   expectation. Gold must be asserted to take the *near-black* label.
3. **No rung's surface is within ΔE 20 of another's.** This is the test that
   would have caught Downloading and Continuing at ΔE 49.
4. **A film's bar never renders a fraction; a show's never renders a tier.** The
   per-medium split is the point of this document and nothing else guards it.
5. **Every switch in the option list produces a visible change on a card.** The
   standing lesson from "declared, never populated" — `titleProgress` computed
   episode progress for a release and nothing drew it, and the legend had an
   Episodes entry behind a prop nothing passed.
6. **`gold-stays-gold.test.ts` extends to the surfaces** — hue 47–52, never below
   52% lightness.

Read every value back *through the thing that consumes it*. Note that
`import css from "../index.css?raw"` returns an empty string under Vitest, so
assertions over it pass vacuously — read the file with `node:fs` and assert its
length first.

---

## 12. Decisions settled for implementation

These are the choices now confirmed against the renderer and live lab:

1. **Depth: Deep, not Jewel.** Jewel puts Continuing at 22% lightness, which
   leaves no headroom and starts to read as black at 5px on a dark card. Deep
   already gives ΔE 76 against Downloading — the problem is solved without
   spending the last of the range.
2. **Treatment: both bars speak, only the subtitle bar is labelled.** §3.
3. **Three switches, named per shelf.** §6. The alternative is one switch called
   something generic, which is what the arrs did and what he objected to.
4. **The remainder is Missing red; the held portion is green, and a fully held
   Quality met title is gold.** This is the TV composition grammar: colour says
   what each segment is and length says how much aired coverage is held. The
   unmonitored override is one flat grey and takes priority (§2, §13).
   *None asked for* is gone (§2), and the subtitle bar still counts — James:
   *"bar should count I was wrong, we are doing it now and no reason to stop it"*.
5. **Continuing goes magenta.** §4.

The top shelf legend keeps the monitoring axis beside, but separate from, the
title-status chips: **Unmonitored sits immediately after Upcoming behind a
divider on both Movies and TV**. The subtitle legend contains only the subtitle
segments; it does not repeat the monitoring override.

**Settled, no longer switches:** the corner pill is deleted and the bars are always
on the artwork — *"corner pill is a complete removal and bars always on artwork"*.
Shipped is out as a palette: it cannot carry a white label at all (1.69:1).

All six are switches on `/renders/card-decider.html`, drawn on the real library.
Flip them, then send the settings line the page prints — it names every one.

## 12a. The audit, and what it caught

Every rule in this document is checked in the browser across every card and every
switch combination, computing what is **visibly rendered** rather than what is set.
That distinction is the whole value of it: a colour on a zero-width element is not on
screen, and reading the style back told me the override was working when it was not.

| | Movies | TV |
|---|---|---|
| Distinct card states | **22** | **44** |
| Card renders checked | 1,104 | 3,240 |
| Switch combinations | 48 | 72 |
| Problems | **0** | **0** |

The catalogue is **enumerated, not hand-picked**: every rung, times every subtitle
state, times monitored and not. A title holding no files has no independent subtitle
state — it inherits the title's own reason for having none — so those rungs
contribute one row rather than four. Artwork repeats where there are more states
than posters, because the picture is not the subject.

Invariants asserted, each one a bug that had already happened:

1. **Unmonitored is grey** — every *visible* region, fill and track, on both bars.
2. **Unmonitored is one grey** — fill and track the same value, so the rung cannot
   change which grey you see.
3. **Grey is never a monitored remainder** — monitored cards use Missing red for
   the remainder, while unmonitored cards remain entirely grey.
4. **Upcoming never says Missing.**
5. **Every visible label clears 4.5:1** against the ground actually under it — the
   fill where the fill is wide enough to be seen, the track otherwise.
6. **Nothing but the image is on the artwork.**
7. **No two cards render the same shape** — compared on fill width, fill colour,
   track colour and label text, not on their names.
8. **No text region is painted twice, and none is left unpainted** — the front
   layer's clip and the back layer's clip must meet exactly at the fill edge.
9. **No subtitle bar ever says Downloading** — the one media state it must not
   borrow.
10. **Every switch changes what is on screen.** Each control is swept from a reset
   baseline and its options must produce N distinct paints. The bars are
   deliberately theme-independent, so `theme` is exempt on the bars and checked on
   the page chrome instead.

Invariant 7 is the one that had been missing, and it is why a duplicate survived a
passing audit: it asserted that scenario *names* were unique, which is a different
question. Two cards drew the identical Quality met / monitored / 100% / 100% and the
check said nothing. James: *"we still have a duplicate quality met - please check
again"*. **An audit that checks the label instead of the thing is the same defect it
is meant to catch.**

The Shipped palette fails #5 on 60 combinations and is excluded from the run, which
is the measured reason it is not a candidate rather than a matter of taste.

---

## 12b. The fill rule is a TV-only decision

Like Continuing, and for the same reason: **a film is one file.**

The fill rule decides how to colour the part you *hold*. A film has no partial
coverage — it is held (100%) or it is not (0%) — and download progress is explicitly
non-compositional, because there is no held part yet. So the rule has nothing to act
on.

Measured across all three fill rules, on the same catalogue:

| | Cards whose style differs | Cards **visibly** different |
|---|---|---|
| Movies | 1 | **0** |
| TV | 14 | **13** |

The single movie card whose style differs is a Missing film, whose fill is 0% wide —
a colour on a zero-width element is not on screen. James, looking at the movie page:
*"this one isnt changing anything at all"*. It was not.

**A switch that cannot change what you are looking at is worse than no switch**: it
invites you to keep flipping it looking for the difference. `Fill` and `Continuing`
are both TV-only controls, and the movie page says why it lacks them rather than
leaving a reader to wonder.

---

## 13. What the filled part is coloured by

James, looking at Severance drawing a flat red bar at 3 of 20: *"why cant we do the
episodes the same as the subs with the bar and number?"* — then, on being shown the
options: *"Im torn with this fill thing now, what does sonarr do?"*

Going and looking gave us the useful comparison, but the product decision is now
the composition grammar: **the held portion is green, a fully held Quality met
title is gold, and the remainder is Missing red**. This is the same semantic
composition already used by the subtitle bar. It keeps both coverage and the
remaining work visible without using grey for a monitored title.

The renderer retains Sonarr's neutral remainder as an exploratory alternative,
but it is not the product default:

| | Track | Fill | Missing title at 3/20 | Continuing, fully held |
|---|---|---|---|---|
| **Deluno composition** ← *recommended* | Missing red | held green; Quality met gold | green sliver, then red | green |
| Sonarr's grammar | neutral remainder; state surface when empty | the state's colour | red sliver, then grey; full red at 0% | magenta |

**Deluno composition** is the approved rule because it reads the same way as the
subtitle bar: colour says what the segment is, and length says how much is held.
Quality met is still unambiguously gold, while Continuing remains available as a
TV-only lifecycle label in the legend and status column.

**Sonarr's grammar** remains useful as the reference implementation: it carries
state in the fill over a neutral track and preserves a state-coloured empty bar.
It is available on the renderer's alternative preset for comparison, not shipped
as the product setting.

**A bar with no fraction** keeps its state's colour under either grammar, because
there is no held part to colour: an Upcoming title has not started, and a downloading
one has no held part yet. Under Deluno composition a zero-percent coverage bar is
simply all Missing red, which is already the correct visible remainder. The
unmonitored override is applied first and paints both layers flat grey.

---

## 14. Rejected, and why

- **Bars under the poster, the arr way** (treatment 4 on the render). It is the
  only arrangement where nothing is written over artwork at all, which is a real
  advantage. Rejected because it puts a strip of chrome under every card and
  costs shelf height on the one screen that most needs to stay dense — and the
  two-tone label makes the wash-out argument moot anyway.
- **`EPS` / `QLTY` leads on the top bar.** §3.
- **One switch covering all three bars.** Fails the one-switch-one-fact rule the
  option list already keeps.
- **Moving Downloading instead of Continuing.** It is the app's primary blue.
- **Darkening gold so it can take white text.** Breaks the 52% floor, and a gold
  below it is bronze.
