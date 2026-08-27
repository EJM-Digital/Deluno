# DESIGN-003 — Filtering, ordering and what a poster carries

James, after the subtitle work landed:

> We don't have a proper filter system like radar to create custom filters for
> views like quality, size or by genre etc etc and sorting is limited as well,
> we also need more options for what the posters can display from the metadata.

Plus, on the same screen: *"The whole all libraries, any monitoring, display,
order, add movie, hunt, refresh and views is all too much on one line and looks
so busy."*

Those are one piece of work, because the answer to the second is where the first
one has to live.

## Named filters, never a rule engine

`CatalogueFilters` is nine typed fields: quality tiers, genres, and ranges for
size, year, runtime and rating. Not a field/operator/value builder.

That is not timidity. **This codebase already shipped the generic version and
deleted it** — `filterAndSortLibraryItems`, `matchesCustomRule`,
`resolveRuleValue` and a 45-value `FilterField` union, in the browser, removed in
[#302](https://github.com/jampat000/Deluno/issues/302). Two of its conditions
tested `MediaItem.status` values nothing ever set, so they matched **zero rows,
forever, silently**. A generic engine can express questions the data cannot
answer, and nothing in it can tell you that it has.

Nine named fields cannot. Each is one stored column with one meaning, applied in
SQL, and adding one means finding a column to read.

## The two things it had to get right

**Filter the row the page speaks for.** Quality and size live on the wanted
state, and a title held in two libraries has two of them.
`CatalogueWantedState.Join` exists precisely so a page displays *one* — its own
header says the eight correlated subqueries it replaced "could not keep their own
answers together". So the predicate reads `ws.*`. Matching on one library's file
while displaying another's would rebuild the exact defect that join removed.

**Count the rows you are showing.** The chips above the shelf and the rows on it
come from two different queries. That is the shape that drifts — the sidebar and
the dashboard disagreeing about "needs you" is the same defect one subsystem
out. The facets query takes the same filters, and
`The_counts_above_the_shelf_count_the_rows_on_it` fails if they part company.

**And it costs nothing unused.** The join the facets query needs is added only
when a filter is asking for it. An unfiltered page runs exactly the query it ran
before this existed — the same rule the subtitle rollup follows.

## Ordering, and the two sorts that are missing on purpose

Added: **Runtime** and **Popularity**. Neither was new to the database. Both
have been indexed since V0011/V0012 and neither had ever been offered — the same
shape as the codec and release-group columns the list displayed for months with
nothing populating them.

**Size and quality are not sortable, and that is the interesting part.**

They live on the wanted state, which the page reaches through
`ws.rowid = (SELECT pick.rowid … LIMIT 1)`. SQLite cannot index that. Ordering by
a column on its far side means running the pick for *every title in the
catalogue* and then sorting the lot: a full scan wearing a seek's clothes,
correct at eleven titles on the lab rig and ruinous at twenty thousand, with
nothing about the result looking wrong. That is precisely the failure
`CatalogueSearchStateOnPageTests` was written to prevent, and shipping it would
have been ignoring our own guard.

Three ways out, none free, for whoever picks this up:

1. **Drive the query from the wanted state when the sort key lives there.**
   `FROM movie_wanted_state ws JOIN movie_entries m …` with an index on
   `(library_id, file_size_bytes, movie_id)` is a genuine seek. It only works
   with a library selected — without one, a title in two libraries appears
   twice — so it would mean "sorting by size picks a library for you", which is
   a rule to explain rather than hide.
2. **Store the picked file's size and quality rank on the entry**, written by
   the same code that writes the wanted state. Fast and indexable, and a second
   copy of a fact, which is the thing this project keeps paying for.
3. **Leave it.** Filtering by size and quality is available and is what people
   actually reach for; ordering by them is a nicety.

Recommended: (1), when somebody wants it enough to accept the rule.

## Genres are served, not guessed

`GET /api/movies/genres` and `/api/series/genres`. One pass over one column, run
when the filter panel opens and never per page. A genre list built from the
current page would offer whatever the first fifty titles happen to be tagged
with and hide the rest of the library without saying so — the same class of
answer as the wanted summary's `LIMIT 25` that made every card past the
twenty-fifth lose its state.

## The toolbar: two rows, one job each

Nine controls competed on one line. They were not wrong individually; there were
too many at the same level. The split is by what you are doing:

| Row | Job | Holds |
|---|---|---|
| **1** | Search, and act | the search box, Add, Hunt, Refresh |
| **2** | Narrow, and arrange | the legend chips, Library, **Filters**, **View** |

Two of those are merges, not moves:

- **Display** and **Order** were one question asked twice — *how do I want to
  look at this* — and are one **View** panel.
- **Monitoring** and **Views** each occupied a whole toolbar control for a
  single setting. Both are inside **Filters** now, beside the quality, size and
  genre filters that never existed.

Nine controls became four plus three actions, and the Filters button carries the
count of everything narrowing the shelf — including the parts you cannot see —
because a narrowed shelf that looks unnarrowed is how somebody loses half their
library and concludes Deluno has.

## What a poster carries

Six new options: size, runtime, genres, release group, codec, added. All off by
default; they are the answer to "show me more", and a card that arrives already
carrying everything has nothing left to ask for.

They share **one truncated line**, not a row each. Six rows would bury the
artwork the grid exists to show.

`DisplayOptions` was declared twice — in `library-grid.tsx` and in
`lib/library-filters.ts` — with the same five fields, in a file whose own header
describes that exact defect. The grid re-exports the one now, and
`parseDisplayOptions` fills in options a stored choice predates, so somebody who
saved their layout last month does not get `undefined` for a switch.

## Still open

- Sorting by size or quality — see above.
- The compact list has fixed columns; the display options only reach the grid.
- Nothing else known. Saved views carry the custom filters too: `rulesJson`
  had a comment saying it was waiting for "a server-side rule contract", and now
  there is one, so it holds a `CatalogueFilters` the server can actually
  perform. Old rows hold `[]` and read back as no filters, which is what they
  meant.
