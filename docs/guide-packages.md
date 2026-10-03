# Guide packages

Deluno ships guide-informed quality data in the backend as an immutable,
versioned package. The package is the source used by the setup guide, quality
profiles, release-rule screens, workers, and external integrations.

## Current package

The current package is exposed read-only at:

```http
GET /api/v1/guides/trash/package
```

It contains:

- quality tiers and their size ranges;
- custom-format definitions with stable IDs, original scores, and source
  provenance (see [Genuine TRaSH formats](#genuine-trash-formats));
- quality profiles and their recommended formats;
- reusable format bundles for common library goals;
- a package version, upstream revision, review date, adaptation note, and
  deterministic SHA-256 integrity value.

The backend package also carries a pinned source inventory for the exact upstream
revision: 478 custom formats, 78 format groups, and 62 quality profiles at the
current revision. It keeps native matcher clauses, per-score-set provenance,
media applicability, and stable group/profile membership. This inventory is
loaded from the backend assembly; it is never fetched while deciding whether to
acquire or replace a release.

The normal package endpoint intentionally omits that 1.5 MB source payload so
ordinary setup screens remain quick. The inventory stays in the backend assembly
and in the stored package, for the update check and the rule-migration code; no
route serves it.

The package is deliberately a Deluno adaptation of TRaSH Guides, not a
Recyclarr input or a verbatim Radarr/Sonarr export. Deluno uses the guide as
reviewed policy input and keeps its typed release-preference contract as the
decision authority.

`GET /api/v1/guides/trash/inventory` returns a deterministic, hashed capability
inventory. Every shipped tier, custom format, matcher clause, profile, bundle,
and pinned upstream group/profile is classified as a typed representation or an
explicit Advanced representation. `Unaccounted` must remain empty; the
persistence test treats a new unexplained guide item as a CI failure rather
than silently dropping it.

## Genuine TRaSH formats

From package version 4 (schema 3), every entry Deluno curates is one of two
kinds, and the package proves which:

- **A genuine TRaSH format** (`sourceKind: trash-guides`). It has TRaSH's own
  id and name for one media type. It is matched by TRaSH's own specifications
  from the pinned inventory, never by patterns Deluno wrote. TRaSH publishes a
  movies copy and a TV copy of most formats under different ids, so the
  package carries both. Deluno adds only the category, a plain-language
  description, the reviewed typed meaning, and a friendly display name in the
  web app.
- **A Deluno-owned extra** (`sourceKind: deluno`). It covers a format TRaSH
  does not publish, such as HDR10 on its own or a remux. Its id starts with
  `deluno-`, it is matched by any one of its patterns, and it serves movies
  and TV.

Validation rejects a genuine entry with patterns or without TRaSH
specifications, and an extra whose id TRaSH already uses.
`GenuineGuideFormatTests` also checks the file as shipped: every genuine
entry's id must be the TRaSH format of that name and media type. Before
version 4 nothing checked this. Of the 85 curated entries, 15 carried the id of
a different TRaSH format ("Amazon Prime" was Crave), and 63 ids were invented.

A rule made from a guide format sends only its guide id. The server writes the
guide's own conditions (`GuideFormatConditions`), so every screen that makes
such a rule gets the same definition.

### Radarr and Sonarr numbering

TRaSH writes each copy in the numbering of the app it is for. `SourceSpecification`
3 is a telecine to Radarr and a WEB-DL to Sonarr. Sonarr also numbers a Blu-ray
remux (7) apart from a Blu-ray encode (6). The matcher reads a movies rule with
Radarr's numbers and a TV rule with Sonarr's.

### What a rename keeps

TRaSH marks each format with `includeCustomFormatWhenRenaming`: its own answer
to whether that format belongs in a file's name. Deluno keeps that flag on the
package entry and uses it for the `{Custom Formats}` token, so a rename carries
exactly the formats the guide says it should, and no list of Deluno's own can
drift from it.

An imported file is named from the decision Deluno made — the quality it
counts as, the formats it matched, the group that made it — never by reading
the incoming file name again. Movies and episodes both have a file-name
pattern; the tokens are `{Quality}`, `{Custom Formats}`, `{Release Group}` and
`{Edition}`, alongside the title and id tokens. An empty token leaves no empty
brackets behind.

### Rules made from the old catalogue

Rules created before version 4 still carry the old ids and Deluno's patterns.
The catalogue that made them is kept as `trash-guide-legacy-catalogue.json`,
which records what each old entry becomes for movies and for TV.

`GET /api/v1/guides/trash/rule-migration` previews the move and changes
nothing. Release Preferences shows it as "Rules from the old guide catalogue".
Each rule keeps its id, score, media type and every profile answer about it.
It takes the genuine format's id and conditions, and takes TRaSH's name only
if the owner never renamed it. When TRaSH has no version of that format for
the rule's media type, the rule stays the owner's own rule and loses its guide
id. A rule whose conditions are specifications is a genuine import that
happens to share an old id, and it is never offered.

`POST /api/v1/guides/trash/rule-migration/apply` moves only the chosen rules,
and only while the preview hash still matches. A rule edited after the preview
is left alone. Each move is recorded in Activity (`quality.custom-format.guide-moved`)
with what the rule was and what it became.

An install that had stored an older package gets Deluno's new layer at startup.
It is stored as a new version over the upstream inventory the owner synced, and
the previous version stays retained.

## Mapping safety

Every custom format declares a mapping status:

- `reviewed` means the format has an explicit mapping to one or more canonical
  typed preference traits;
- `advanced` means the matching rule remains available to the custom-format
  surface but must not be silently treated as a typed release preference.

An Advanced source rule exposes its original stable ID, score set and native
matcher clauses for inspection. It cannot contribute an additive score to a
typed plan. A later reviewed mapping is a deliberate package change, not a
silent reinterpretation of an older plan.

The backend validates the package at startup. It rejects duplicate IDs,
unknown tier/profile/bundle references, invalid regular expressions, and
unknown typed trait mappings. This makes a package update fail visibly rather
than changing acquisition decisions by accident.

## Updating the package

The owner-approved update flow is versioned and read-before-write:

1. Review the upstream TRaSH custom-format/profile and quality-definition
   changes at the pinned upstream revision.
2. For a source-snapshot update from the Settings UI, first run the opt-in
   change check and choose **Preview sync**. Deluno downloads the archive for
   that exact immutable Git revision, retains every supported source item,
   preserves existing reviewed mappings, and leaves unfamiliar upstream rules
   as Advanced. `POST /api/v1/guides/trash/sync/preview` returns the normal
   versioned package diff without saving it.
3. Choose **Sync reviewed snapshot** only after reviewing that diff. The
   endpoint rebuilds the candidate from the same immutable revision and checks
   the current and proposed package hashes before
   `POST /api/v1/guides/trash/sync/apply` persists it. This updates no local custom
   format, library profile, scenario plan, or release decision; plan changes
   remain a separate preview-and-apply action.
4. For an independently prepared reviewed package, submit the candidate to
   `POST /api/v1/guides/trash/preview` with
   `expectedCurrentIntegritySha256` from the current package. The response
   validates schema, hashes every capability, compiles every profile, and
   returns typed-plan/Advanced diffs and warnings.
5. Apply the exact reviewed candidate with
   `POST /api/v1/guides/trash/apply`. Deluno rejects stale previews, invalid or
   unaccounted mappings, and changed content reusing an old package version.
6. Inspect `GET /api/v1/guides/trash/versions` to retain the immutable active
   and rollback-capable package history. Existing local quality profiles are
   not overwritten by a guide package update.
7. Run the package contract, inventory, compiler, and endpoint inventory tests.

The embedded package remains the bootstrap value until the owner applies an
update. Package compilation is read-only and uses the active persisted package;
release decisions never fetch guide content from the network.

## Optional upstream-change checks

`GET /api/v1/guides/trash/update-check` exposes a local, default-off setting
and the most recent report. When the owner enables it, Deluno may run one check
per week; `POST /api/v1/guides/trash/update-check/run` runs the same check on
demand. The check reads the public TRaSH Git tree and compares its blob IDs
with the pinned source paths in the active package. It reports changed or
removed custom formats, groups, and quality profiles, marks changes that touch
saved custom formats, and lists new JSON files in the guide directories.

This is deliberately **not** an automatic sync: it does not download an
upstream package, rewrite a local custom format, alter a quality profile, or
change a release decision. When the report finds a change, **Preview sync**
offers the separate, versioned owner-approved workflow above. Disabling the
setting prevents outbound guide-check requests; the last report remains local
for review.

Upstream references:

- [TRaSH Guides repository](https://github.com/TRaSH-Guides/Guides)
- [TRaSH custom-format collection](https://trash-guides.info/Radarr/Radarr-collection-of-custom-formats/)
- [TRaSH quality-profile guidance](https://trash-guides.info/Radarr/radarr-setup-quality-profiles/)

To refresh the pinned source inventory after reviewing a new upstream commit:

```powershell
.\scripts\generate-trash-guide-source-inventory.ps1 -SourceRoot <checked-out-TRaSH-Guides-revision>
```

The script refuses an unexpected revision. Update its expected revision and the
package provenance together, then run the package and inventory tests before
proposing the package update.
