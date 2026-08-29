# DESIGN-006 — The title card, per medium

Settled 2026-08-30 with the owner. Supersedes the card half of
[DESIGN-001](DESIGN-001-title-marks.md) and the poster half of the Option A
decision in `4bdfe45`. The subtitle vocabulary from
[DESIGN-002](DESIGN-002-subber.md) is unchanged and inherited.

Rendered reference, drawn at real shelf sizes in both themes:
`/renders/bars-that-speak.html` on the rig
(source: `ui-explorations/bars-that-speak.html`).

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
| Back text | the whole bar | muted, chosen for the empty track |
| Fill | 0–N% of the bar | the mark's surface colour |
| Front text | clipped to exactly N% | chosen for the fill |

Both text layers are the **same string in the same position**. Only the paint of
the front one is clipped. A bar that is 15% full therefore has 15% of its label
in white and 85% in grey, and every glyph is coloured for the ground directly
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
| **When nothing is held** | the state's word — `Missing`, `Upcoming` | `0 / 29`, empty track |
| **Bottom bar says** | `SUBS 1 / 3` | `SUBS 2 / 6` |
| **Bottom bar counts over** | its one file | the episodes you actually hold |

### Why the fill means two different things

For a **show** the fill is coverage: how much of what has aired is on disk. For a
**film** there is nothing to be partway through — you have it or you do not — so
the fill is free to mean **download progress**, which is the one time a film is
genuinely part-way. A held film is a solid bar; a downloading film fills as the
bytes arrive; a missing film is an empty track.

This is not an inconsistency to apologise for. Each medium's bar fills with the
only fraction that medium has.

### The subtitle bar counts only files you hold

Unchanged from DESIGN-002 and restated because it is easy to lose: a show short
of episodes is already saying so on its top bar, in red, on the same card.
Dragging the subtitle bar down for the same reason would be the same fact twice.

---

## 3. Anatomy

```
┌───────────────────────────┐
│ ▓▓▓░░░░░ 3 / 20 ░░░░░░░░░ │  ← media bar, 14px, top edge
│                           │
│         artwork           │
│                           │
│  Severance                │
│ ▓▓▓▓▓ SUBS 2 / 6 ░░░░░░░░ │  ← subtitle bar, 14px, bottom edge
└───────────────────────────┘
```

Both bars are **14px**, full bleed, on the card's top and bottom edges.

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
| Empty track | `--mark-idle`, per theme | muted, per theme | — |

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

### Continuing moves off Downloading's blue

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

## 5. Monitoring

Unchanged. A rung that `canBeHalf` and is not monitored draws the *fill* as
`linear-gradient(90deg, <surface> 0 50%, <idle> 50% 100%)`. The label is
unaffected — it is already two-toned, and the half is a property of the fill.

---

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

**Bars are never switched off, only their words.** With every switch off the card
is what ships today: two coloured bars and no text. The state mark stays
mandatory, per the owner on DESIGN-001.

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
4. **The corner pill is deleted.** §7. It is the only removal here, and it is
   removed because the bar now says what it said.

## 13. Rejected, and why

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
