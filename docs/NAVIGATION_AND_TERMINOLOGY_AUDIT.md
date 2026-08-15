# Deluno navigation and terminology audit

Updated: 2026-08-14

This is the source-of-truth audit for visible navigation. It distinguishes a
destination a person chooses from a detail/action screen reached while working
with an item. No configuration destination may exist only behind a contextual
link.

## Daily media work

| Visible destination | Purpose | Child routes / wording contract |
| --- | --- | --- |
| Dashboard | At-a-glance state and the next useful action. | Add a movie, Add a show, setup readiness, attention. It is not a fake activity feed. |
| Movies | Work with the movie collection. | Library, Wanted, Upgrades, Needs attention. Movie detail screens belong to the selected movie. |
| TV Shows | Work with the TV collection. | Library, Wanted, Upgrades, Needs attention. Episode search and show detail belong to the selected show. |
| Schedule | See releases and retry windows. | Calendar context only; it does not duplicate automation configuration. |
| Transfers | Follow work moving through download, processing, import, and recovery. | Download progress, processing handoffs, import jobs, client history, and recovery are one lifecycle. |
| Automation | Observe and control work Deluno is doing now. | Search runs, retries, queues, and per-library pauses. It is an operational screen, not the home for permanent policy. |
| Activity | Read the durable record of what happened and why. | History only; it is not a live-control screen. |

## Library setup

| Visible destination | What it changes | Visible children |
| --- | --- | --- |
| Library | How Deluno reaches completed files, optionally waits for processed output, and imports the result. The parent opens the Files, processing & import landing page. | Processing & import; Final destinations; Metadata & sidecars; Tags |
| Connections | Search sources, external download clients, file-location translation when paths differ, and per-library routing. | Indexers; Download clients; File locations; Library routing |
| Media plans & quality | The selection policy that ties quality, size, release traits, upgrades, and destinations together. | Media Plan; Quality profiles; Quality & size limits; Release scoring |
| Discover media | Which watchlists and curated lists should feed Deluno. | Import lists |
| Automation & recovery | The standing rules for searches, retries, upgrades, and failed downloads. | Search, retries & failed downloads |

### Connection terminology

- **Search source** means an indexer, RSS source, or other service Deluno asks
  for releases.
- **Download client** means the app that receives an approved release and
  downloads it, such as SABnzbd, NZBGet, qBittorrent, or Transmission.
- **File locations** is the plain-language name for the technical remote-path
  mapping. It sits on the relevant download client and is only needed when that
  client and Deluno see the same completed files at different paths.
- Deluno does not download files itself. External download clients own protocol
  work, queueing, repair, unpacking, retention, and seeding; Deluno dispatches,
  observes, imports, routes, and recovers media around them.
- **Queue removal permission** lives with Download clients. It only enables a
  confirmed manual removal from an external client; the automatic three-strike
  recovery policy remains in Automation & recovery.

### Quality terminology

- **Media Plan** is Deluno’s high-level policy: the outcome a library wants.
- **Quality profile** is the familiar Radarr/Sonarr-style ladder and upgrade
  target assigned to a library.
- **Quality & size limits** defines accepted quality tiers and sensible file
  size bounds.
- **Release scoring** prefers or avoids release traits. The page explains that
  this is compatible with the familiar “Custom Formats” concept.

## Maintain Deluno

Installation-wide choices belong here, never in Library setup.

| Visible destination | Purpose |
| --- | --- |
| System & settings | Entry point and health overview for this installation. |
| System activity | System-level events and audit trail. |
| Backups | Backup schedule, restore preview, and downloads. |
| Updates | Version status and update/restart workflow. |
| API access | Keys for trusted integrations and scripts. |
| Help & guides | Plain-English configuration and lifecycle guidance. |
| General | Instance identity, network address, port, and reverse-proxy routing. |
| Notifications | Outbound webhook destinations and events. |
| Interface | Theme, density, and display defaults. |
| Migration | Preview and import supported external configurations. |
| Guided setup | The step-by-step starting flow. |

## Intentional non-navigation routes

These are reachable from the relevant parent and must not be promoted as global
menu items: individual movie/show detail, add-media sheets, episode search,
manual import, the specific Usenet/torrent-engine forms, and legacy redirects.
They preserve context instead of creating another navigation branch.

## Guardrails

1. Desktop sidebar and mobile More drawer expose the same named destinations.
2. A parent label navigates; its chevron only expands or collapses children.
3. “Built-in,” implementation names, and internal architecture terms do not
   appear as primary user navigation labels.
4. Every new configuration route must be added to either Library setup or
   Maintain Deluno before it can ship. Operational routes must live under What
   Deluno is doing and must not be the only route to a permanent setting.
