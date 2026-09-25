# Tracker definitions

Deluno searches torrent trackers itself (#606): no Prowlarr, no Jackett.
Each tracker is described by one small YAML file — a *definition* — that
tells Deluno's tracker engine how to search the site, read its results, and
turn a release into a magnet or a `.torrent`. This page documents the fields
the engine reads, in Deluno's own words, so a tracker can be added or fixed
without a Deluno release.

## Where definitions live

- **Built in**: `src/Deluno.Integrations/Search/Trackers/Definitions/*.yml`,
  compiled into Deluno as embedded resources. This is the starter set Deluno
  ships (see below).
- **User-supplied**: the `trackers` folder under Deluno's data directory
  (the `DataRoot` setting; `data/trackers` by default). Drop a `<id>.yml`
  file there and it replaces the built-in definition of the same id, or adds
  a new tracker entirely — Deluno picks it up without a rebuild or a
  restart of the release, because `TrackerDefinitionCatalog` reads this
  folder every time the catalog loads. This is what makes "adding a tracker
  needs no Deluno release" true.

A definition that fails to parse, or that uses something the engine cannot
do (see **What the engine does not do**, below), is left out of the list a
person can pick from; it never crashes Deluno.

## Writing a definition

A definition is a single YAML mapping. The engine reads it field by field —
there is no schema file to generate against, so this page is the reference.

### Identity

| Field | Meaning |
|---|---|
| `id` | Stable, lower-case identifier. Indexer rows reference a tracker by this id, so it must never change once shipped. |
| `name` | Display name. |
| `description` | One sentence shown on the tracker picker. |
| `language` | BCP-47 language tag; defaults to `en-US`. |
| `type` | `public`, `semi-private`, or `private`. Only `public` skips the "share carefully" warning. |
| `encoding` | The character set to decode a response as when the response itself does not say (its `Content-Type` header or an HTML `<meta charset>` always wins first). |
| `links` | One or more base addresses. Deluno tries each in turn when one does not answer. |
| `followredirect` | Whether to follow HTTP redirects when fetching a page. |
| `requestDelay` | Seconds to wait between requests to this tracker, honouring its rate limit. |

### Settings and sign-in

`settings` lists the fields a person fills in when adding this tracker:
`text`, `password`, `checkbox`, `select` (with `options`), `multi-select`, or
an `info*` type, which shows a message and stores nothing (used for the
"this site may need a Cloudflare helper" notice — see below).

`login`, when present, describes how to sign in: `method` (`post`, `form`,
`cookie`, `get`, or `oneurl` — anything else is treated as a definition the
engine cannot run), `path`, `inputs` (name to templated value), `error`
selectors that mean the sign-in failed, and a `test` page/selector that
proves a session is still valid. A `captcha` block marks a sign-in the
engine cannot do unattended; such a definition is still listed, marked as
unsupported, with the reason shown.

### Search

`search.paths` is one or more requests to try, each with a `path` (relative
to the tracker's link), optional `method`, `inputs`, and a `response.type`
of `html` (default), `xml`, or `json`. `search.inputs` are inputs common to
every path. `search.headers` sets request headers.

`search.rows` says how to find one result per row:

- HTML/XML: `selector` (a CSS selector for each row), optional `remove`
  (strip matching children before reading fields), `after` (merge this many
  following rows into the one before them, for sites that split a release
  across two `<tr>`s), and `dateheaders` (a date printed once above a group
  of rows rather than on each one).
- JSON: `selector` is a dotted path (`data.movies`, or `$` for the response
  root), with an optional `attribute` to read a nested array off each
  matched object (`torrents`, with `multiple: true`) — this is how one API
  entry becomes several releases, one per quality. `count` is a path to a
  result-count field; a definition can short-circuit to no results without
  reading further.

`search.fields` maps a field name to how to read it: a `selector` (CSS or
JSON path) with an optional `attribute`, or a literal `text` template. Every
field supports `filters` (see below), `optional` (a field that need not be
on every row), and `default`. Fields are read in the order they are
written, and every value is kept as `{{ .Result.name }}` for the fields
after it — this is how, for example, a `title` field can be built from
several other fields read first.

The field **names** the engine understands and what they set on a release:
`title`, `details`/`comments` (the human results page), `download` (a URL,
or a `magnet:` link recognised by its scheme), `magnet`, `infohash`, `size`,
`seeders`, `leechers`, `grabs`, `files`, `date`, `category`/`categorydesc`
(mapped through `caps.categorymappings`, below), `imdb`/`imdbid`, `tmdbid`,
`tvdbid`, `year`, `genre`, `downloadvolumefactor`, `uploadvolumefactor`,
`minimumratio`, `minimumseedtime`, `poster`, `description`. Any other name
is just kept as an intermediate value for later fields to read — as
`title_long` and `quality` are used in `yts.yml` to build `title`.

A row needs a title and either a magnet, a download URL, or an info hash
(from which Deluno builds a magnet itself) to become a release; anything
else is skipped, not fatal to the rest of the page.

### Categories

`caps.categorymappings` is a list of `{id, cat, desc}`: the tracker's own
category id or name (`id`), and which of Deluno's standard categories it
means (`cat`, one of the names in `TrackerCategories.Standard` — `Movies`,
`Movies/HD`, `TV`, `TV/Anime`, and so on). A definition with no mapping into
the `Movies` or `TV` family is not offered for movie or TV libraries.
`caps.modes` lists which query parameters a search mode accepts (for
example `movie-search: [q, imdbid]`); the engine only sends a parameter the
tracker says it takes.

### Two-step downloads

Some trackers (LimeTorrents, 1337x) only link a search result to a details
page; the real magnet or `.torrent` is on that page, sometimes behind a
Cloudflare check the results page was not. Point the `download` field at
the details page, then add a top-level `download` block:

```yaml
download:
  selectors:
    - selector: "a[href^='magnet:']"
      attribute: href
```

Deluno fetches the details page when a release is grabbed and tries each
selector in turn until one yields a magnet or a working `.torrent`.
`download.before` fetches one more page first (for a tracker that needs a
confirmation click before the link appears); `download.infohash` reads a
hash and title off the page instead, when a site never shows a magnet
directly.

### Filters

A field's value can be run through `filters`, applied in order — each an
`{name, args}` pair (`args` a single value or a list):

`regexp` (first capture group), `re_replace` (find/replace), `split`,
`replace`, `trim`, `prepend`, `append`, `tolower`, `toupper`, `urldecode`,
`urlencode`, `htmldecode`, `htmlencode`, `dateparse`/`timeparse` (a .NET
date layout), `timeago`/`reltime`/`fuzzytime`, `validfilename`,
`diacritics`, `validate`, `jsonjoinarray`, `hexdump`, `strdump`,
`querystring` (a value out of a URL's query string).

`search.rows.filters` (row-level, not per-field) supports `andmatch` (drop a
row whose title does not contain every search word) and `strdump`.

### Templates

Any `path`, `inputs` value, `text` field, filter argument, and a few other
places accept a small template language: `{{ .Keywords }}`,
`{{ .Query.IMDBID }}`, `{{ .Config.<setting> }}`, `{{ .Result.<field> }}`,
`{{ .True }}`/`{{ .False }}`, `if`/`else if`/`else`/`end`, `range`, and the
functions `and`, `or`, `not`, `eq`, `ne`, `join`, `re_replace`, `len`,
`print`.

## What the engine does not do

`TrackerDefinitionValidator` checks a definition when it loads and marks it
unsupported, with a plain-English reason, rather than letting it fail on
first use, when it: has no search path; signs in with a method Deluno does
not implement; needs a captcha solved by hand; uses a template the engine
cannot parse; or uses a filter or row filter the engine does not have. An
unsupported definition still shows on the tracker picker, disabled, with
the reason as its tooltip.

## The starter set

Deluno ships a small set of public trackers, each written from that site's
own public API or pages — never copied from an indexer manager's
definitions, because Deluno carries no licence file that would allow it:

| id | Site | Built from |
|---|---|---|
| `yts` | YTS | Its public JSON API (`api/v2/list_movies.json`) |
| `eztv` | EZTV | Its public JSON API (`api/get-torrents`), which only answers by IMDb id |
| `thepiratebay` | The Pirate Bay | The `apibay.org` public JSON API |
| `limetorrents` | LimeTorrents | Its own search results page (HTML) |
| `1337x` | 1337x | Its own search results page (HTML); usually needs a Cloudflare helper |
| `nyaasi` | Nyaa.si | Its own search results page (HTML) |
| `rutor` | Rutor | Its own search results page (HTML) |
| `torrentdownloads` | TorrentDownloads | Its own search results page (HTML) |
| `internetarchive` | Internet Archive | Its own public `advancedsearch.php` JSON API; every item's torrent lives at a fixed, predictable address |

Adding another public tracker is writing one more file in this format from
that site's own API or pages, dropping it in the data folder's `trackers`
directory (or contributing it to the built-in set), and nothing else.
