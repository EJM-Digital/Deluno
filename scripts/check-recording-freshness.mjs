#!/usr/bin/env node
// Recording freshness (issue 1277, docs/testing/STRATEGY.md "Tiers", measure 4): compares the newest version recorded for each app under
// tests/fixtures/recorded/<app>/<version>/ with the newest release of that app on GitHub, and lists the apps whose recording is a
// major version, or more than one minor version, behind (the same rule as scripts/check-component-pins.mjs). The nightly workflow
// freshness-nightly.yml opens one defect issue naming them, so the app is re-recorded with its one command
// (scripts/recorded-replies/Invoke-RecordRealApp.ps1 or Invoke-RecordReplies.ps1).
//
//   node scripts/check-recording-freshness.mjs           print the table; exit 1 when an app is stale
//   node scripts/check-recording-freshness.mjs --json    print the stale apps as JSON on the last line
//
// Apps with a dated folder (tmdb, thexem, gestdown, github) are services, not versions: they are never stale here. An app whose
// project is not in the table below is listed as not checked, never silently passed.
import { existsSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isStale, parseVersion } from './check-component-pins.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));

/** The GitHub project that publishes each app's releases. */
export const PROJECTS = {
  deluge: 'deluge-torrent/deluge',
  flaresolverr: 'FlareSolverr/FlareSolverr',
  gotify: 'gotify/server',
  jackett: 'Jackett/Jackett',
  jellyfin: 'jellyfin/jellyfin',
  ntfy: 'binwiederhier/ntfy',
  nzbget: 'nzbgetcom/nzbget',
  nzbhydra2: 'theotherp/nzbhydra2',
  prowlarr: 'Prowlarr/Prowlarr',
  qbittorrent: 'qbittorrent/qBittorrent',
  radarr: 'Radarr/Radarr',
  rtorrent: 'rakshasa/rtorrent',
  sabnzbd: 'sabnzbd/sabnzbd',
  seerr: 'seerr-team/seerr',
  sonarr: 'Sonarr/Sonarr',
  transmission: 'transmission/transmission'
};

/** Services and Deluno's own side of Weir: not an app version to keep up with. */
export const NOT_APPS = new Set(['deluno', 'weir', 'github', 'gestdown', 'thexem', 'tmdb', 'emby', 'plex']);

export function newestRecorded(versions) {
  const parsed = versions.map((version) => ({ version, parsed: parseVersion(version) })).filter((v) => v.parsed);
  parsed.sort((a, b) => a.parsed.major - b.parsed.major || a.parsed.minor - b.parsed.minor || a.parsed.patch - b.parsed.patch);
  return parsed.length ? parsed[parsed.length - 1].version : null;
}

export function recordedApps(recordedRoot = join(root, 'tests', 'fixtures', 'recorded')) {
  if (!existsSync(recordedRoot)) return [];
  return readdirSync(recordedRoot)
    .filter((app) => statSync(join(recordedRoot, app)).isDirectory())
    .map((app) => ({ app, versions: readdirSync(join(recordedRoot, app)).filter((v) => statSync(join(recordedRoot, app, v)).isDirectory()) }));
}

export async function latestRelease(project, fetchImpl = fetch) {
  const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'deluno-recording-freshness' };
  if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  const response = await fetchImpl(`https://api.github.com/repos/${project}/releases/latest`, { headers });
  if (response.status === 404) {
    // A project that publishes tags and no releases (Deluge): its newest tag is the newest version.
    const tags = await fetchImpl(`https://api.github.com/repos/${project}/tags?per_page=30`, { headers });
    if (tags.ok) {
      const names = (await tags.json()).map((tag) => String(tag.name)).filter((name) => parseVersion(name) && !/(?:a|b|rc|dev)\d*$/i.test(name));
      if (names.length) return newestRecorded(names);
    }
  }
  if (!response.ok) throw new Error(`${project}: GitHub answered ${response.status}`);
  const body = await response.json();
  return String(body.tag_name ?? body.name ?? '');
}

/** One row per recorded app: { app, recorded, latest, state } with state ok, stale, not-checked or unreadable. */
export async function check(apps, lookup = latestRelease) {
  const rows = [];
  for (const { app, versions } of apps) {
    if (NOT_APPS.has(app)) continue;
    const recorded = newestRecorded(versions);
    const project = PROJECTS[app];
    if (!recorded || !project) { rows.push({ app, recorded, latest: null, state: 'not-checked' }); continue; }
    try {
      const latest = await lookup(project);
      rows.push({ app, recorded, latest, state: isStale(recorded, latest) ? 'stale' : 'ok' });
    } catch (error) {
      rows.push({ app, recorded, latest: null, state: 'unreadable', detail: String(error.message ?? error) });
    }
  }
  return rows;
}

async function main() {
  const rows = await check(recordedApps());
  for (const r of rows) console.log(`${r.state.padEnd(11)} ${r.app.padEnd(13)} recorded ${r.recorded ?? '-'}  newest ${r.latest ?? '-'}${r.detail ? `  (${r.detail})` : ''}`);
  const stale = rows.filter((r) => r.state === 'stale');
  if (process.argv.includes('--json')) console.log(JSON.stringify(stale));
  if (stale.length) {
    console.error(`\n${stale.length} recording(s) are behind the newest release: ${stale.map((s) => s.app).join(', ')}.`);
    process.exit(1);
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) await main();
