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
- custom-format definitions with stable IDs, original scores, matching
  patterns, and source provenance;
- quality profiles and their recommended formats;
- reusable format bundles for common library goals;
- a package version, upstream revision, review date, adaptation note, and
  deterministic SHA-256 integrity value.

The package also carries a pinned source inventory for the exact upstream
revision: 478 custom formats, 78 format groups, and 62 quality profiles at the
current revision. It keeps native matcher clauses, per-score-set provenance,
media applicability, and stable group/profile membership. This inventory is
loaded from the backend assembly; it is never fetched while deciding whether to
acquire or replace a release.

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
2. Submit the candidate package to `POST /api/v1/guides/trash/preview` with
   `expectedCurrentIntegritySha256` from the current package. The response
   validates schema, hashes every capability, compiles every profile, and
   returns typed-plan/Advanced diffs and warnings.
3. Apply the exact reviewed candidate with
   `POST /api/v1/guides/trash/apply`. Deluno rejects stale previews, invalid or
   unaccounted mappings, and changed content reusing an old package version.
4. Inspect `GET /api/v1/guides/trash/versions` to retain the immutable active
   and rollback-capable package history. Existing local quality profiles are
   not overwritten by a guide package update.
5. Run the package contract, inventory, compiler, and endpoint inventory tests.

The embedded package remains the bootstrap value until the owner applies an
update. Package compilation is read-only and uses the active persisted package;
release decisions never fetch guide content from the network.

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
