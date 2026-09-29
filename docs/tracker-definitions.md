# Tracker definitions

Deluno searches torrent trackers itself (#606, #986): no Prowlarr, no Jackett.
Each tracker is described by one small YAML file — a *definition* — that
tells Deluno's tracker engine how to search the site, read its results, and
turn a release into a magnet or a `.torrent`. This page documents the fields
the engine reads, in Deluno's own words, so a tracker can be added or fixed
without a Deluno release.

## Where definitions live

- **The built-in site list (#986)**: a snapshot of Prowlarr's public tracker
  definitions (`Definitions/Prowlarr/*.yml`, Cardigann schema version 11, from
  the Prowlarr/Indexers repository), 568 sites: 81 public, 60 semi-private and
  427 private, embedded in Deluno so every site is usable at once, with no
  download step. They are read as they are; Deluno did not write them. Their
  origin and terms (synced from Jackett, GPL-2.0; the Prowlarr repository has no
  licence file of its own) are in `Definitions/Prowlarr/NOTICE.md`, served at
  `/api/trackers/definitions/notice` and linked from the tracker picker and
  Settings, General, Tracker definitions. Deluno's own engine (not Prowlarr's
  or Jackett's code) runs them.
- **Two Deluno-written files kept**: `src/Deluno.Integrations/Search/Trackers/Definitions/*.yml`
  are `eztv` and `torrentdownloads`, kept over the list's file of the same id
  because each does something the list's file cannot (see below). One of
  these wins over a built-in list file with the same id, and an update never
  replaces it.
- **User-supplied**: the `trackers` folder under Deluno's data directory
  (the `DataRoot` setting; `data/trackers` by default). Drop a `<id>.yml`
  file there and it replaces the built-in definition of the same id, or adds
  a new tracker entirely — Deluno picks it up without a rebuild or a
  restart of the release, because `TrackerDefinitionCatalog` reads this
  folder every time the catalog loads. This is what makes "adding a tracker
  needs no Deluno release" true.

A definition that fails to parse, or that uses something the engine cannot
do (see **What the engine supports and what it does not**, below), is listed
on the picker as not supported, with the reason in words, and cannot be
chosen; it never crashes Deluno.

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

## What the engine supports and what it does not (#986)

Measured against the 568 definitions in the built-in snapshot (the
repository's `definitions/v11` folder on 28 September 2026); a test asserts
these counts:

- **Loaded and runnable: 523 of 568.** Each is offered in the Add indexer list.
  Every definition's templates parse, every filter it names exists, every CSS
  selector it uses is understood, and a search request builds for each of them
  (checked against every one of the 523 with a stand-in site).
- **Listed but not usable yet: 45**, all private sites whose sign-in shows a
  captcha picture that has to be read by a person (52pt, btschool, carpt,
  chdbits, ...). They are listed at the bottom of the picker with that reason
  in words, never hidden. Nothing else stops any definition from loading.
- A file that cannot be read at all is listed the same way ("Deluno could not
  read this definition file") instead of silently dropping out.

What is implemented, from the definitions themselves:

- **Templates**: `{{ .Keywords }}`, `.Query.*` (IMDBID, IMDBIDShort, TMDBID,
  TVDBID, Season, Ep, Year, Type, Q...), `.Categories`, `.Config.*`,
  `.Result.*`, `.True`/`.False`, `.Today.Year`, `.DownloadUri.*`; `if`/`else
  if`/`else`/`end`, `range`; the functions `and`, `or`, `not`, `eq`, `ne`, `join`,
  `re_replace`, `len`, `print`. Those are all the actions and functions the
  568 definitions use (checked by counting them).
- **Filters**: querystring, dateparse, timeparse, regexp, re_replace, split,
  replace, trim, prepend, append, tolower, toupper, urldecode, urlencode,
  htmldecode, htmlencode, timeago, reltime, fuzzytime, validfilename,
  diacritics, validate, jsonjoinarray, hexdump, strdump; row filters andmatch
  and strdump; keyword and preprocessing filters.
- **Sign-in**: `form`, `post`, `get`, `oneurl`, `cookie` (a cookie copied from a
  browser, with the browser's User-Agent when the definition asks for one),
  hidden form fields and `selectorinputs`, error selectors, a test page that
  proves the session, and signing in again when the session runs out. Not
  supported: a `captcha` block.
- **Results**: HTML, XML and JSON (nested arrays, `multiple`, `count`,
  `case`, `default`, `optional`, `remove`, date headers, rows split over
  two lines).
- **Downloads**: a magnet, a .torrent, a details page with selectors, a
  "before" request (resolved against the site, not the release page), a hash
  read from a page and turned into a magnet.
- **Self-signed certificates**: a definition's `certificates` fingerprint is
  trusted for that site only.
- **Cloudflare**: through the existing helper (FlareSolverr or Byparr), the
  same one Deluno's kept files use.
- **Passwords and keys**: any setting named like a password, token, key,
  cookie or two-factor code is stored encrypted and never shown back.

What the site list does not contain: **Usenet**. Prowlarr's YAML set
has no Usenet indexers (Newznab indexers are added by address and API key,
which Deluno already has as the Newznab indexer kind).

`TrackerDefinitionValidator` checks a definition when it loads and marks it
unsupported, with a plain-English reason, rather than letting it fail on
first use, when it: has no search path; signs in with a method Deluno does
not implement; needs a captcha solved by hand; uses a template the engine
cannot parse; or uses a filter or row filter the engine does not have.

## Updating (off by default)

Once the list is downloaded, the picker's "Check for newer site files" button
and Settings, General, Tracker definitions fetch newer copies from the
Prowlarr/Indexers repository (`definitions/v11`), once on request or on a
schedule. The schedule is off on a new install and on an existing one. A
changed file is written to the data folder's `trackers` directory, and
Activity names each definition that was added or updated. A failed check
(no internet, GitHub down) changes nothing and tries again on its own
schedule.

## Deluno's own sites, and what became of them (#986)

Deluno used to ship nine hand-written public sites. Each was compared with the
list's definition of the same site (same id in all nine cases), using the
pages and API answers Deluno had recorded for its own file:

| id | List file | Result | Why |
|---|---|---|---|
| `yts` | `yts` | dropped | Same JSON API; the list's file read the recorded answer identically |
| `thepiratebay` | `thepiratebay` | dropped | Same `apibay.org` API, identical results |
| `limetorrents` | `limetorrents` | dropped | Same page, identical results |
| `1337x` | `1337x` | dropped | The list's file also handles the Cloudflare helper, download fallbacks and uploader filter |
| `nyaasi` | `nyaasi` | dropped | Identical results, plus the sort and compatibility settings |
| `rutor` | `rutor` | dropped | Same page; the list's file reads the hash and the .torrent link |
| `internetarchive` | `internetarchive` | dropped | Same `advancedsearch.php` API, more settings |
| `eztv` | `eztv` | **kept** | Ours uses EZTV's JSON API by IMDb id, exact for a film or show; the list's file only searches by keyword on the HTML site, behind Cloudflare, with no IMDb-id mode |
| `torrentdownloads` | `torrentdownloads` | **kept** | Ours reads the site's RSS search feed, which carries the info hash, so no details page is fetched per grab; the list's file scrapes HTML and needs one |

An indexer saved against one of the seven dropped ids keeps that id and its
settings and now runs from the list's file (the id map is
`TrackerSiteMapping.Retired`). After an upgrade, one Activity line names the
indexers that moved ("Deluno now runs your ... indexers from the built-in
tracker site list instead of its own site files. Their settings were kept.").
An indexer that cannot be matched (its site is not in the list, or a setting
saved for it does not exist in the site's file) is left exactly as it is and
shows on Health in plain words; it clears when the indexer is fixed or
removed. Nothing is ever deleted.

Adding another tracker is writing one more file in this format, dropping it in
the data folder's `trackers` directory, and nothing else.
