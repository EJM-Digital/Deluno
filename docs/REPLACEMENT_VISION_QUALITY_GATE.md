# Deluno replacement-vision quality gate

This is the product-quality gate for recommending Deluno as a safe, simpler replacement for the combined workflows of Radarr, Sonarr, SABnzbd/qBittorrent, Prowlarr, Huntarr, Configarr, Recyclarr, and CleanUpArr. It complements the GA execution checklist in `docs/ga-release-checklist.md`; it does not replace installer, release-candidate, or soak evidence.

This is a final-state contract, not a v1/v2 split. Deluno is not considered
ready because a partial surface resembles one of the tools above. The complete
supported workflow must be configured, exercised, recoverable, explainable,
and evidenced. The canonical workflow and setup journey are documented in
`docs/DELUNO_END_TO_END_WORKFLOW.md` and `docs/DELUNO_SETUP_ORDER.md`.

## Decision rule

Deluno is ready to recommend for a scenario only when the user can configure that scenario, understand what will happen, complete the expected flow, recover safely from failure, and inspect the evidence afterward. A passing build alone is never enough.

## Evidence matrix

| Area | Required evidence | Current evidence | Release decision |
| --- | --- | --- | --- |
| Product purpose and boundaries | North-star, explicit supported/unsupported behaviour | `docs/PRODUCT_NORTH_STAR.md`; Library setup outcome map | Defined; must be reviewed against the candidate |
| Movie and TV acquisition | Repeatable accepted/rejected candidate → dispatch → import → rename → catalog trail | `scripts/test-real-world-flows.ps1`; isolated movie/torrent and TV/NZB fixtures | Automated fixture proof only; live supported-provider proof remains #93 |
| First run | Server-backed progress and non-secret draft; browser path; deliberate manual-only route | Setup persistence tests and desktop/mobile smoke coverage | Needs full supported-provider completion in #93 |
| Policy transparency | Explain why a release is accepted, held, rejected, or force-overridden | Title decision trails, quality/custom-format tests | Canonical Media Plan model remains #88 |
| Automation | Visible current/next/retry/paused state and safe controls | Automation view, library/global controls, browser workflow checks | Title defer/override and deeper state transitions remain #90 |
| Imports and recovery | Preflight, no overwrite, idempotent retry, recovery evidence | Import preview, recovery UI, collision/rename tests, real-world fixtures | Fault injection/reconciliation acceptance remains #92 |
| Cleanup | No automatic deletion without ownership, evidence, and approval | Queue health findings are observation-only and explicitly non-destructive | Durable policy/retention/remediation remains #95 |
| Migration | Preview-first, idempotent mapping, safe rollback and report | Existing migration assistant | Canonical mapping/model work remains #89 after #88 |
| Security and operations | Auth checks, local binding, redacted support diagnostics, dependency review | Existing auth/local defaults and system diagnostics | Candidate-time vulnerability and support-bundle review required |
| Packaging and release | Clean install, upgrade/rollback, backup/restore, regression, soak, final notes | `docs/ga-release-checklist.md`, RC matrix, release evidence template | #81, #82, #85, #86, and #78 require real candidate evidence |

## Mandatory scenario proof

For every supported scenario, record the configuration, expected outcome, actual outcome, and recovery evidence:

1. Movie-only, TV-only, and mixed libraries.
2. Manual-only library management with intentionally skipped connections.
3. Supported torrent and Usenet connection paths.
4. Accepted, held, rejected, and manual-force release decisions.
5. Move, hardlink, and copy import behavior, including collision and restart recovery.
6. A paused library, paused instance, retry window, and safe resume.
7. Download-health findings and any remediation proposal with a human approval boundary.
8. Existing-library/configuration migration without overwrite.

## Non-negotiable safety rules

- Never delete or overwrite library media automatically.
- Never claim a source, download client, import, or release is healthy/complete without the corresponding real check.
- Never persist setup credentials in a resumable setup draft.
- Never make a user infer whether a setting is daily media work or advanced Library setup configuration.
- Never label fixture-only verification as live provider proof.

## Closure evidence for the GA epic

The GA epic (#78) can close only when each matrix row is either proven for the released scope or explicitly removed from the supported scope and reflected in the product, documentation, and release notes. The final evidence links must include the real-provider flow (#93), the final policy decision (#88), and the GA execution evidence (#81, #82, #85, and #86).
