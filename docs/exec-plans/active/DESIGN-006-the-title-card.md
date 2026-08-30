# DESIGN-006 — The title card, per medium

Settled 2026-08-30 with the owner. Supersedes the card half of
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

  One page per shelf, because one page carrying both was too hard to read — the owner:
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

The owner: *"there is a 'show text on progress bar' for both sonarr and radarr —
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

**Implementation note that is not optional.** Clip the front layer with
`clip-path: inset(0 <100-N>% 0 0)` over an identically positioned element. Do
**not** make the front container `width: N%` and centre the text inside it — the
label then centres on the *fill* instead of the *bar* and slides sideways as the
bar fills. That was the first attempt here and it is visibly wrong at 3 of 20.

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
| **When nothing is held** | the state's word — `Missing`, `Upcoming` | `0 / 29`, no fill |
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

**Neutral. Measured in Sonarr, not reasoned about.**

An earlier revision of this document asserted the remainder should be Missing red,
on the argument that *the part you do not have* is Missing and the subtitle bar's
`titleBarGradient` already ends in red. That argument is tidy and it is wrong, and
it caused the defect it then needed two more rules to repair: with the remainder
red, a Missing title's fill and remainder were the same colour and the fraction
vanished — Severance at 3 of 20 drew the same flat bar as Foundation at 0 of 29.

Read out of Sonarr's own DOM, every poster, no exceptions:

| Series state | Track | Fill | Fill width |
|---|---|---|---|
| Continuing, all held | grey `rgb(91,91,91)` | blue `rgb(93,156,236)` | 100% |
| Ended, all held | grey `rgb(91,91,91)` | green `rgb(39,194,76)` | 100% |
| Missing episodes | grey `rgb(91,91,91)` | **red** `rgb(240,80,80)` | **86%** |

**The colour is the state and the length is the fraction.** A series missing
episodes is a red bar filled to how much you hold. Both facts on one bar, neither
lost, and it works precisely *because* the track is neutral. Deluno had this right
before the red remainder was introduced.

#### The one gap, and it closes for free

At 0% fill a neutral bar is wholly grey, so its state is drawn nowhere — and Deluno
has deleted the corner pill that used to carry it. Sonarr has the same gap and gets
away with it because its poster carries nothing else either.

So **on the neutral track the label wears the state's own text colour**: Foundation's
`0 / 29` is red on grey. The *text* token, not the surface — surfaces are tuned for
white-on-bar, text tokens for reading on a ground (§4). The fill still means exactly
what Sonarr's means, and nothing is spent to keep the state.

### The subtitle bar counts only files you hold

Unchanged from DESIGN-002 and restated because it is easy to lose: a show short
of episodes is already saying so on its top bar, in red, on the same card.
Dragging the subtitle bar down for the same reason would be the same fact twice.

### "None asked for" is not a state

A title whose `subtitleLanguagesWanted` is zero was drawn as a grey bar reading
*none asked for*, which invented a fourth thing a subtitle bar can be. The owner:
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

The owner: *"upcoming is the wrong colour, its missing yes but its upcoming too"*.

The rule above said *not here is Missing*, and applied it to every title. That is
wrong, and it threw away the one distinction the ladder exists to make: **Missing
means it is out and you do not have it.** An Upcoming title is not out. You cannot
be missing a subtitle for a file that cannot exist yet.

So when a title holds **no files at all**, the subtitle bar inherits the title's own
reason for having none:

| Title | Its subtitle bar reads |
|---|---|
| Upcoming | `SUBS Upcoming`, in violet |
| Downloading | `SUBS Downloading`, in blue |
| Missing | `SUBS Missing`, in red |
| Held, languages short | `SUBS 1 / 3` — a real count |

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
the artwork** — settled by the owner: *"corner pill is a complete removal and bars always
on artwork"*.

**The label is 11.5px at weight 700**, not 10px at 900. Two separate faults: at 10px
`WEBDL-1080p` was too small even where contrast passed — Sonarr and Radarr both set
12px on a 15px bar — and the `<b>` inside a 700 parent resolved to **900**, which is
what made white on a saturated bar bloom. The owner: *"almost like its overexposed"*. The
tag is there for structure, so it is pinned back to 700.

**And the label is a hard argument against the Shipped palette.** White on the
shipped dark green is **1.69:1** — not marginal, illegible. Deep gives 5.49 and Jewel
7.36. A ladder that cannot carry a white label is a ladder that cannot carry this
design, so Shipped is out on measurement rather than taste.

### How a reader knows which is which

Three things say it, in the order they are noticed:

1. **The bottom bar leads with the word `SUBS`. The top bar does not lead with
   anything.** The asymmetry is the signal: one bar names its subject, the other
   is obviously about the title itself. This is the whole of the owner's *"be more
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

The owner: *"more obvious change with deeper proper colours"*.

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
| Continuing | `hsl(178 96% 24%)` | white | 5.33 |
| Quality met | the existing `--mark-leaf-*` gradient | **near-black** `hsl(40 90% 12%)` | 10.46 |
| Upcoming | `hsl(270 76% 47%)` | white | 7.22 |
| Remainder (track) | `--mark-idle`, per theme | the **state's text colour** — see §2 | — |
| Unmonitored half | `--mark-idle`, per theme | — | — |

**Gold's label is the one asymmetry, and it is forced.** Gold is floored at 52%
lightness — below that yellow reads as bronze, which is the whole of
`gold-stays-gold.test.ts` — so white can never sit on it. Every other bar takes
white; gold takes near-black. Do not "fix" this by darkening gold.

### Text — what a count or a word is coloured with

Per theme, unchanged from what ships today except Continuing, which has to move.

| Rung | Light | Dark |
|---|---|---|
| Missing | `hsl(0 84% 48%)` | `hsl(0 84% 62%)` |
| Downloading | `hsl(207 92% 45%)` | `hsl(207 96% 62%)` |
| Upgradable | `hsl(145 72% 34%)` | `hsl(145 78% 52%)` |
| Continuing | `hsl(178 74% 30%)` | `hsl(178 76% 50%)` |
| Quality met | `hsl(42 96% 40%)` | `hsl(44 98% 58%)` |
| Upcoming | `hsl(268 62% 50%)` | `hsl(268 82% 72%)` |

### The wheel is not full — Continuing goes magenta

The owner: *"I really wish we could find a better colour for continuing... have we
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

The pair the owner flagged was Continuing and Upcoming. Measured, those two are the
*furthest apart* on the ladder (ΔE 88). The pair that actually collides is
**Continuing and Downloading** — ΔE 49, hues 184 and 207, the smallest gap of any
two rungs and both a bright cool blue. He confirmed that is the one he was
looking at.

Continuing moves 184 → 178 and, as a surface, down to 24% lightness. Downloading
does not move: it is the app's primary.

| Pair | Before | After |
|---|---|---|
| Downloading vs Continuing, surface | ΔE 49 | **ΔE 76** |
| Downloading vs Continuing, text (dark) | ΔE 49 | **ΔE 61** |
| Upgradable vs Continuing, surface | — | ΔE 33 |

Upgradable vs Continuing at ΔE 33 is the new nearest pair and is comfortably
clear; it is recorded here so the next person moving a hue knows which neighbour
they are moving toward.

### Pre-existing, not fixed here

Several **light-theme text** tokens already fall short of AA on white and this
document does not change them: Downloading 4.20, Upgradable 3.85, Continuing
4.46, Quality met 2.89. Gold's is deliberate — it is the dark "Quality met"
count. The others predate this work. **The deep surfaces do not make any of them
worse**, and fixing them is its own issue, not a silent rider on this one.

---

## 5. Monitoring — its own mark, on the poster

DESIGN-001 gives an unmonitored title a **half-grey dot**. An earlier revision of
this document carried that over and halved the *bar's fill* instead. That is wrong:
a half works on a **dot** because a dot has no length of its own, and **a bar is a
length** whose length already means the fraction you hold. The render proved it
before the argument did — on a Missing title, whose fill is 0% wide, the half
rendered as *nothing at all*.

The next attempt overrode the bars' colour entirely when unmonitored — black or
grey, both measured clear of the track. It worked, and it spent the bar's colour on
a fact that is not about the title's state.

**The answer is a separate mark.** The owner: *"maybe a nice little monitored /
unmonitored on the poster is the best play here"*. The bars go on saying what the
title *is*; whether Deluno is watching it is a different fact and gets a different
mark — a small opaque badge in the corner of the artwork.

**Shown only when it is OFF.** Nearly every title is monitored, so a badge on every
card says nothing on almost all of them, and the one card that needs to stand out
stops standing out. An exception is worth marking; a default is not. The badge is
opaque for the same reason the label is two-toned: a translucent mark is a different
colour on every poster.

**The glyph is a shield**, because Deluno already has one: `library-grid.tsx` draws
`ShieldCheck` / `ShieldOff` for monitoring. The render first reached for an eye —
which reads as *watching* in English but is not the app's own word for it, and a
second icon for a fact that already has one is how an icon language stops being a
language.

`canBeHalf` in `TITLE_MARK_PRESENTATION` therefore has no consumer on the card any
more. Do not delete it blindly — the drawer and detail page may still read it — but
check, because a flag nothing reads is the shape of the defects this project keeps
finding.

## 5a. What actually sits on the card

**Only the two bars and the monitoring badge.** Nothing else.

The title was first drawn over the picture behind a gradient — which is not what the
shelf does. The owner: *"we shouldnt have the title on the poster we dont even have that
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
survive. The state mark stays mandatory, per the owner on DESIGN-001.

These are user switches, not design decisions, so **all four combinations are cards
a real person will see** — the owner: *"some of these options are selectable for on and
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
| `apps/web/src/index.css` | Six `--mark-*-surface` tokens, theme-independent. `--mark-airing` → hue 178, both themes. Gold untouched. |
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

## 12. Decisions the owner still has to confirm

He asked for the document rather than picking from the render, so these are
chosen here with reasons and are his to overturn:

1. **Depth: Deep, not Jewel.** Jewel puts Continuing at 22% lightness, which
   leaves no headroom and starts to read as black at 5px on a dark card. Deep
   already gives ΔE 76 against Downloading — the problem is solved without
   spending the last of the range.
2. **Treatment: both bars speak, only the subtitle bar is labelled.** §3.
3. **Three switches, named per shelf.** §6. The alternative is one switch called
   something generic, which is what the arrs did and what he objected to.
4. **The remainder is a neutral track and the fill is the state's colour**, which
   is what Sonarr does and was measured rather than argued (§2, §13). An earlier
   revision of this document got that backwards and caused a defect with it.
   *None asked for* is gone (§2), and the subtitle bar still counts — the owner:
   *"bar should count I was wrong, we are doing it now and no reason to stop it"*.
5. **Continuing goes magenta.** §4.

**Settled, no longer switches:** the corner pill is deleted and the bars are always
on the artwork — *"corner pill is a complete removal and bars always on artwork"*.
Shipped is out as a palette: it cannot carry a white label at all (1.69:1).

All six are switches on `/renders/card-decider.html`, drawn on the real library.
Flip them, then send the settings line the page prints — it names every one.

## 13. What the filled part is coloured by

The owner, looking at Severance drawing a flat red bar at 3 of 20: *"why cant we do the
episodes the same as the subs with the bar and number?"* — then, on being shown the
options: *"Im torn with this fill thing now, what does sonarr do?"*

Going and looking settled it, and settled §2 with it. Sonarr colours the **fill by
the state** and fills it to the **fraction held**, over a neutral track. There is no
tension to resolve: the flat bar was caused by the red remainder, not by the fill
rule, and removing the red remainder removes the need for any of the alternatives.

Two coherent grammars remain, both one click on the decider page:

| | Track | Fill | Missing title at 3/20 | Continuing, fully held |
|---|---|---|---|---|
| **Sonarr's grammar** ← *recommended* | neutral | the state's colour | red sliver, then grey | magenta |
| Composition, like SUBS | none | what you hold | green sliver, then red | **green — Continuing's colour gone** |

*Composition* is the more internally consistent of the two and the more expensive: a
fully-held show is green whether it is Continuing or Upgradable, so Continuing loses
its colour on the shelf entirely, which would make the whole magenta question in §4
moot.

*Sonarr's grammar* is recommended because it carries both facts on one bar, it is
proven in the app Deluno is replacing, and with the tinted track label (§2) it has no
remaining blind spot.

**A bar with no fraction** keeps its state's colour under either grammar, because
there is no held part to colour: an Upcoming title has not started, and a downloading
one has no held part yet. `mediaBar` says whether there is a fraction outright — the
first attempt inferred it from `pct > 0 && pct < 100`, which quietly excluded a
fully-held Continuing show, the single case the composition rule exists to show, so
that rule silently rendered identically to the one beside it.

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
