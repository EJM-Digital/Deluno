# Deluno Product Map

Updated: 2026-04-21

## Core Product Areas

Deluno should feel like one premium app with three clearly separate working areas:

- `Movies`
- `TV Shows`
- `Indexers`

These are not the same thing, and the product should not blur them together.

## What Each Area Owns

### Movies

Owns:

- movie libraries
- movie-specific root folders
- movie rules and quality targets
- movie catalog, wanted state, upgrades, imports, and history

From the user perspective this is:

- where I manage my movie collection
- where I choose movie library behavior
- where I understand movie wanted and upgrade status

### TV Shows

Owns:

- TV libraries
- TV-specific root folders
- standard, daily, anime, and special-case show handling
- show, season, and episode state
- TV wanted state, upgrades, imports, and history

From the user perspective this is:

- where I manage my TV collection
- where TV workflows can differ from Movies without confusion
- where episode-specific edge cases are explained clearly

### Indexers

Indexers is Deluno's source, routing, and outside-service layer.

Owns:

- indexers
- download clients
- connection health
- release-source routing
- mapping which libraries use which outside services

From the user perspective this is:

- where Deluno connects to the outside world
- where I decide which services support Movies or TV Shows
- where I fix unhealthy services without hunting through unrelated settings

This should absorb the role that Prowlarr-style tooling often plays, but in Deluno's language and product shape.

Recurring missing and upgrade searching should not appear to the user as a separate product area. That behavior belongs inside Movies and TV Shows as normal Deluno options.

## Product Rules

- Movies and TV Shows are first-class product sections, not tabs hanging off one generic library page.
- Indexers is a Deluno area, not an afterthought hidden in Settings.
- Recurring search behavior should feel built into Movies and TV Shows, not like a separate helper app.
- User-facing language should always say `TV Shows`, never `Series`.
- Internal modules can stay separate even when the UI feels unified.

## Backend Ownership

Recommended ownership model:

- `Deluno.Movies`: movie catalog, movie library rules, movie wanted and upgrade state
- `Deluno.Series`: TV Shows engine internally, even if the UI always says TV Shows
- `Deluno.Broker` or `Deluno.Integrations`: indexers, download clients, routing, health
- `Deluno.Automation` or `Deluno.Jobs`: recurring scheduling, cooldown memory, backlog runs, and activity as internal infrastructure

## Frontend Navigation Direction

Primary navigation should eventually make this separation obvious:

- Overview
- Movies
- TV Shows
- Indexers
- Activity
- Settings

Secondary views can sit inside each area, for example:

- Movies: Library, Wanted, Upgrades, Imports, Search automation
- TV Shows: Library, Wanted, Upgrades, Imports, Search automation
- Indexers: Indexers, Download Clients, Health

## Why This Matters

Users should never have to mentally translate:

- "am I changing a movie rule or a TV rule?"
- "is this about services or about my library?"
- "is this a one-time search or an ongoing Deluno rule for this section?"

If Deluno keeps those mental models clean, it will feel more premium and more trustworthy than the current split-tool approach.
