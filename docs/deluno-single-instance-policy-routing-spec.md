# Deluno Single-Instance Policy Routing Spec

## Why This Exists

Radarr and Sonarr users regularly run multiple instances for reasons that are not really about tenancy. They do it because one install cannot express enough acquisition and destination policy inside a single library model.

The common reasons are:

- 4K and 1080p variants of the same title
- anime needing different quality, naming, and routing rules
- kids or family content needing separate root folders
- language-specific variants
- different retention or upgrade behaviour by content class

Deluno should solve that inside one install.

## Product Goal

One Deluno instance should be able to express:

- where a title should land
- what quality policy applies to it
- whether more than one version should be kept
- how it should be searched and upgraded

That means the answer is not "more instances." The answer is policy-driven routing.

## Settings IA

Settings should be grouped by decisions, not by a flat list of technical pages.

### Overview

- configuration health
- quick actions
- setup posture
- links into the main control areas

### Library

- Media Management
- Metadata
- Tags
- later: Destination Rules

### Quality

- Profiles
- Quality Sizes
- Custom Formats
- later: Policy Sets
- later: Multi-Version Targets

### Automation

- Lists
- later: Search Behaviour
- later: Intake Sources

### System

- General
- Interface

## New Core Concepts

### Destination Rules

Rules that decide the final root or folder destination for a title or version.

Possible inputs:

- media type
- genre
- tag
- language
- quality class
- anime / non-anime
- certification
- library purpose

Possible outputs:

- root folder
- folder naming template
- metadata export profile

Examples:

- Movies with `Animation` genre -> `D:\Media\Kids Movies`
- TV with tag `anime` -> `E:\Anime Series`
- 4K movie versions -> `F:\Movies 4K`

### Policy Sets

A policy set is the high-level acquisition profile applied to a title or title-version.

It should reference:

- quality profile
- custom formats
- upgrade behaviour
- destination rules
- search cadence overrides

Examples:

- `Standard 1080p`
- `Premium 4K`
- `Anime Dual Audio`
- `Kids Safe Library`

### Multi-Version Targets

The same movie or show can have more than one retained target inside one Deluno install.

Examples:

- keep 1080p and 4K
- keep dubbed and original audio
- keep a mobile-friendly version and a premium home-theater version

This should not require a second Deluno instance.

### Title Policy Assignments

Titles should be assignable to:

- one primary policy set
- optionally one or more additional retained targets

That lets the library UI explain why a title is routed or upgraded the way it is.

## Backend Model Direction

These domains do not all need to be built immediately, but they are the correct shape.

### destination_rules

- `id`
- `name`
- `priority`
- `media_type`
- `match_json`
- `root_path`
- `folder_template`
- `metadata_profile`

### policy_sets

- `id`
- `name`
- `media_type`
- `quality_profile_id`
- `destination_rule_id`
- `upgrade_until_cutoff`
- `search_interval_override`
- `retry_delay_override`

### policy_set_custom_formats

- `policy_set_id`
- `custom_format_id`
- `weight_override`

### title_policy_assignments

- `id`
- `media_type`
- `title_id`
- `policy_set_id`
- `is_primary`

### title_version_targets

- `id`
- `title_policy_assignment_id`
- `variant_key`
- `destination_rule_id`
- `quality_profile_id`

## UX Direction

The user should not have to understand database shape to use this.

The app should present:

- Library destinations
- Quality policies
- Automation sources
- System behaviour

Advanced routing should appear as:

- human-readable rules
- previews of where a title will land
- explicit explanations in title detail

## Near-Term Implementation Order

1. Keep the simplified Settings IA.
2. Add `Destination Rules` under `Settings > Library`.
3. Add `Policy Sets` under `Settings > Quality`.
4. Add per-title policy assignment in Movies and TV workspaces.
5. Add multi-version targets after single-policy routing is stable.

## Product Standard

If a user would normally install a second Radarr or Sonarr instance to solve a routing or quality problem, Deluno should aim to solve that inside one install with policy.
