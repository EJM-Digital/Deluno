# Deluno UI Backlog

Updated: 2026-04-21

This backlog is intentionally product-facing. It tracks the UX debt we already know about so backend progress does not bury it.

## Navigation and Information Architecture

- Make `Movies` and `TV Shows` feel like top-level product areas with their own supporting views.
- Replace the current generic connections surface with a proper `Indexers` area.
- Move recurring search controls into Movies and TV Shows instead of presenting them like a separate helper tool.
- Remove any remaining internal or technical wording that leaks implementation details.

## Libraries Page

- Add edit-in-place support for library automation rules.
- Explain automation status in plainer language with less status-badge ambiguity.
- Separate folder setup from automation behavior so the page reads faster.
- Improve the layout for long paths and dense library setups.
- Make multi-library setups feel premium instead of form-heavy.

## Import Recovery

- Add first-class failed-import recovery surfaces for Movies and TV Shows.
- Show clear failure classes like quality rejected, unmatched, corrupt, download failed, and import failed.
- Provide plain-language explanations and safe actions per class.
- Turn import recovery into a premium review workflow rather than a raw maintenance screen.

## Activity

- Turn the current list into a richer timeline with better grouping and scannability.
- Distinguish background checks, grabs, imports, skips, and attention items more clearly.
- Hide technical detail by default and reveal it only when needed.

## Dashboard

- Replace placeholder stats with true wanted, upgrade, and library-health counts.
- Add clearer "what Deluno is doing next" storytelling.
- Highlight Movies and TV Shows separately so users can orient themselves quickly.

## Settings and Connections

- Split product configuration from service setup more clearly.
- Move indexers and download clients toward a dedicated Broker experience.
- Reduce the amount of raw settings language shown up front.

## General Polish

- Improve table and list density handling for large libraries.
- Add better empty states with clearer next actions.
- Refine responsive behavior for narrower desktop windows and tablets.
- Tighten form spacing, validation presentation, and success feedback.
- Improve consistency of buttons, pills, and badges across pages.
