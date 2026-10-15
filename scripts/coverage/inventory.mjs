// Everything Deluno exposes or runs, read straight from the source: the inventory the coverage register must account for.
//
//   node scripts/coverage/inventory.mjs            print the count of every kind
//   node scripts/coverage/inventory.mjs --list     print every item (kind, key, where)
//   node scripts/coverage/inventory.mjs --json     print the whole inventory as JSON (--no-scenarios skips reading the golden-path plan)
//
// Kinds (each item has a `key`; a register row names the keys it covers in `covers`):
//   route        route:GET /api/movies/{id}        every Map* call in src (minimal-API route groups resolved)
//   webroute     web:/movies/:id                   every route and redirect in apps/web/src/router.tsx
//   service      service:DelunoHeartbeatWorker     every hosted service (BackgroundService / IHostedService / AddHostedService)
//   lane         lane:import.files                 every job lane (JobLanes.All)
//   handler      handler:MoviesCatalogRefreshJobHandler   every job handler (IJobHandler)
//   task         task:rss.sync                     every system task (SystemTasks.All)
//   healthcheck  healthcheck:IndexerHealthCheck    every health check (IHealthCheck)
//   migration    migration:movies/V0007            every database migration
//   integration  integration:qbittorrent           every integration client, and
//                integration:qbittorrent.GrabAsync each of its operations
//   uisurface    ui:components/app/job-drawer.tsx          every menu, drawer and dialog component of apps/web/src
//   uiaction     ui:components/app/job-drawer.tsx#Button:Retry   every control inside one: a button, link, switch, field or picker with its
//                                                          accessible name, its words or, when it has neither, its handler
//   tray         tray:PauseResume                  every tray menu action (TrayMenuId)
//   scenario     scenario:film.qbittorrent         every golden-path scenario id (ScenarioPlan.ps1)
//
// No dependencies; the logic of the audit's route and migration scripts, rewritten in node.
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { basename, dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { slash, walk } from './testindex.mjs';

const readCs = (file) => readFileSync(file, 'utf8').replace(/^﻿/, '');
const csFiles = (root, dir) => walk(join(root, dir), (f) => f.endsWith('.cs'));
const rel = (root, file) => slash(relative(root, file));
const lineAt = (text, pos) => text.slice(0, pos).split('\n').length;

// ------------------------------------------------------------------------------------------------------------- routes

function findClose(s, i) {
  let depth = 0;
  let j = i;
  const n = s.length;
  let inString = false;
  while (j < n) {
    const c = s[j];
    if (inString) {
      if (c === '\\') { j += 2; continue; }
      if (c === '"') inString = false;
    } else if (c === '"') inString = true;
    else if (c === '/' && s[j + 1] === '/') {
      const k = s.indexOf('\n', j);
      j = k === -1 ? n : k;
      continue;
    } else if (c === '(') depth++;
    else if (c === ')') {
      depth--;
      if (depth === 0) return j;
    }
    j++;
  }
  return n - 1;
}

function chainEnd(s, j) {
  let k = j + 1;
  for (;;) {
    const re = /\s*\.\s*(\w+)(<[^>(]*>)?\s*\(/y;
    re.lastIndex = k;
    const m = re.exec(s);
    if (!m) return k;
    k = findClose(s, re.lastIndex - 1) + 1;
  }
}

function firstString(arg) {
  const m = /^\s*(?:"([^"]*)"|\$"([^"]*)"|(string\.Empty)|(\w[\w.]*))/.exec(arg);
  if (!m) return null;
  if (m[1] !== undefined) return m[1];
  if (m[2] !== undefined) return m[2];
  if (m[3]) return '';
  return '<' + m[4] + '>';
}

const API_PREFIX_FILES = ['MonitoringEndpoint', 'Network/LanAccess', 'Network/Connectivity', 'Network/BundledComponent', 'System/System'];

export function routeItems(root) {
  const raw = [];
  for (const file of csFiles(root, 'src')) {
    const s = readCs(file);
    if (!/\.\s*Map(?:Get|Post|Put|Delete|Patch|Methods|Group)\s*\(/.test(s)) continue;
    const relFile = rel(root, file);
    const gdefs = [];
    for (const m of s.matchAll(/(?:var\s+(\w+)\s*=\s*)?(\w+)\s*(?:\r?\n\s*)?\.\s*MapGroup\s*\(/g)) {
      const op = m.index + m[0].length - 1;
      const cl = findClose(s, op);
      const chain = s.slice(cl, chainEnd(s, cl));
      gdefs.push({ varName: m[1], parent: m[2], prefix: firstString(s.slice(op + 1, cl)), anon: chain.includes('AllowAnonymous'), pos: m.index });
    }
    const groups = new Set(gdefs.filter((g) => g.varName).map((g) => g.varName));
    const resolveGroup = (variable, pos) => {
      let prefix = '';
      let cur = variable;
      let at = pos;
      for (let seen = 0; groups.has(cur) && seen < 10; seen++) {
        const candidates = gdefs.filter((g) => g.varName === cur && g.pos < at);
        if (candidates.length === 0) break;
        const g = candidates.reduce((a, b) => (b.pos > a.pos ? b : a));
        prefix = (g.prefix || '') + prefix;
        cur = g.parent;
        at = g.pos;
      }
      return { prefix, root: cur };
    };
    for (const m of s.matchAll(/(\b[\w.]+?)\s*\.\s*Map(Get|Post|Put|Delete|Patch|Methods)\s*\(/g)) {
      const receiver = m[1].split('.').pop();
      const op = m.index + m[0].length - 1;
      const cl = findClose(s, op);
      const path = firstString(s.slice(op + 1, cl));
      const { prefix, root: groupRoot } = resolveGroup(receiver, m.index);
      let full;
      if (path === null) full = null;
      else if (path) full = prefix + (!path.startsWith('/') && prefix ? '/' : '') + path;
      else full = prefix;
      raw.push({ verb: m[2].toUpperCase(), path: full, file: relFile, line: lineAt(s, m.index), root: groupRoot });
    }
  }
  const items = [];
  const seen = new Set();
  const push = (verb, path, file, line) => {
    let p = path.replace(/\{(\w+)(:[^}]*)?\}/g, '{$1}');
    if (p.length > 1) p = p.replace(/\/+$/, '');
    const key = `${verb} ${p}`;
    if (seen.has(key)) return;
    seen.add(key);
    items.push({ kind: 'route', key: `route:${key}`, label: key, file, line });
  };
  for (const r of raw) {
    if (r.path === null) continue;
    let p = r.path;
    if (r.file.startsWith('src/Deluno.Api/') && (p.startsWith('/monitoring') || p.startsWith('/system'))) p = '/api' + p;
    else if (
      API_PREFIX_FILES.some((k) => r.file.includes(k)) && r.file.startsWith('src/Deluno.Api/') && r.root === 'endpoints'
      && !r.file.includes('Backup') && !r.file.includes('Downloads') && !r.file.includes('Health') && !r.file.includes('Calendar')
      && !p.startsWith('/api')
    ) p = '/api' + p;
    if (p.startsWith('<route>')) {
      for (const base of ['/api/movies/{id}/libraries', '/api/series/{id}/libraries']) push(r.verb, base + p.slice('<route>'.length), r.file, r.line);
      continue;
    }
    push(r.verb, p, r.file, r.line);
  }
  return items;
}

// ------------------------------------------------------------------------------------------------------------ web routes

export function webRouteItems(root) {
  const file = join(root, 'apps/web/src/router.tsx');
  if (!existsSync(file)) return [];
  const text = readCs(file);
  const start = text.indexOf('createBrowserRouter(');
  if (start < 0) return [];
  const body = text.slice(start);
  const items = new Map();
  const frames = [];
  const fullPath = (own) => {
    let p = '';
    for (const f of frames) if (f.path != null && f !== own) p = join2(p, f.path);
    return p;
  };
  const join2 = (a, b) => (b.startsWith('/') ? b : (a.endsWith('/') ? a : a + '/') + b);
  const add = (path, own, redirect) => {
    const full = path === null ? (fullPath(own) || '/') : join2(fullPath(own), path);
    const key = (full === '' ? '/' : full).replace(/\/+$/, '') || '/';
    if (!items.has(key) || (items.get(key).redirect && !redirect)) items.set(key, { redirect });
  };
  for (let i = 0; i < body.length; i++) {
    const c = body[i];
    if (c === '{') frames.push({ path: null, index: false, redirect: false });
    else if (c === '}') {
      const f = frames.pop();
      if (!f) continue;
      if (f.path != null) add(f.path, f, f.redirect);
      else if (f.index) add(null, f, f.redirect);
    } else if (frames.length) {
      const top = frames[frames.length - 1];
      const rest = body.slice(i, i + 200);
      let m;
      if (/^\s/.test(c)) continue;
      if ((m = /^path:\s*"([^"]*)"/.exec(rest))) { top.path = m[1]; i += m[0].length - 1; }
      else if (/^index:\s*true/.test(rest)) top.index = true;
      else if (/^element:\s*<Navigate\b/.test(rest) || /^element:\s*<Legacy\w*Redirect\b/.test(rest)) top.redirect = true;
    }
  }
  const out = [];
  for (const [path, info] of [...items].sort((a, b) => a[0].localeCompare(b[0]))) {
    if (path === '*' || path.endsWith('/*')) continue;
    out.push({ kind: 'webroute', key: `web:${path}`, label: path + (info.redirect ? ' (redirect)' : ''), file: 'apps/web/src/router.tsx', redirect: info.redirect });
  }
  return out;
}


// -------------------------------------------------------------------------------------- menus, drawers and dialogs (web)
//
// A menu, drawer or dialog component is a .tsx file under apps/web/src that renders a Drawer, Sheet, ConfirmDialog, any *Dialog, any
// *Menu*, a Popover or a MenuSelect, or has role="menu" or role="dialog". Each is a `uisurface` item, and every control inside it is a
// `uiaction` item named by what a person (or a screen reader) reads on it. Read straight from the source like the rest, so a new
// drawer, dialog or menu item cannot arrive without a register row: measure 4 of docs/testing/STRATEGY.md.

const SURFACE_MARK = /<(?:Drawer|Sheet|ConfirmDialog|\w*Dialog|\w*Menu\w*|Popover|MenuSelect)\b|role=["'](?:menu|dialog)["']/;
const ACTION_TAGS = ['Button', 'button', 'DrawerDanger', 'Switch', 'Checkbox', 'Select', 'MenuSelect', 'Input', 'Textarea', 'NavLink', 'Link', 'a', 'SegmentedControl', 'Chip', 'Disclosure'];

/** The end of a JSX opening tag that starts at `from` (the index of its "<"), skipping braces and quotes in attributes. */
function tagEnd(s, from) {
  let depth = 0;
  let quote = null;
  for (let i = from + 1; i < s.length; i++) {
    const c = s[i];
    if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
    if (c === '"' || c === "'" || (c === '`' && depth > 0)) { quote = c; continue; }
    if (c === '{') depth++;
    else if (c === '}') depth--;
    else if (c === '>' && depth === 0 && s[i - 1] !== '=') return i;
  }
  return -1;
}

/** The literal words in JSX children: expressions in braces and nested tags are dropped. */
function literalWords(children) {
  let out = '';
  let depth = 0;
  for (const c of children) {
    if (c === '{') depth++;
    else if (c === '}') depth = Math.max(0, depth - 1);
    else if (depth === 0) out += c;
  }
  return out.replace(/<[^>]*>/g, ' ').replace(/<[\s\S]*$/, ' ').replace(/&\w+;/g, ' ').replace(/\(\s*\)/g, ' ').replace(/\s+/g, ' ').trim();
}

function actionLabel(attrs, words) {
  let m;
  if ((m = /aria-label=(?:"([^"]+)"|\{\s*["'`]([^"'`$]+)["'`]\s*\})/.exec(attrs))) return m[1] ?? m[2];
  if (words) return words;
  if ((m = /\b(?:label|title|placeholder|aria-label)=(?:"([^"]+)"|\{\s*["'`]([^"'`$]+)["'`]\s*\})/.exec(attrs))) return m[1] ?? m[2];
  if ((m = /\b(on(?:Click|Select|Change|CheckedChange|ValueChange|Toggle))=\{\s*(?:\(\s*[^)]*\)\s*=>\s*)?(\w+)/.exec(attrs))) return `${m[1]}:${m[2]}`;
  if ((m = /\bto=(?:"([^"]+)"|\{\s*["'`]([^"'`$]+)["'`])/.exec(attrs))) return `to:${m[1] ?? m[2]}`;
  return null;
}

export function uiItems(root) {
  const base = join(root, 'apps/web/src');
  if (!existsSync(base)) return [];
  const out = [];
  const files = walk(base, (f) => f.endsWith('.tsx') && !/\.test\.tsx$/.test(f)).sort();
  for (const file of files) {
    const s = readCs(file);
    if (!SURFACE_MARK.test(s)) continue;
    const where = slash(relative(base, file));
    const surfaceKey = `ui:${where}`;
    out.push({ kind: 'uisurface', key: surfaceKey, label: where, file: rel(root, file) });
    const seen = new Set();
    const re = new RegExp(`<(${ACTION_TAGS.join('|')})(?=[\\s/>])`, 'g');
    for (const m of s.matchAll(re)) {
      const end = tagEnd(s, m.index);
      if (end < 0) continue;
      const selfClosing = s[end - 1] === '/';
      const attrs = s.slice(m.index + m[0].length, selfClosing ? end - 1 : end);
      let words = '';
      if (!selfClosing) {
        const close = s.indexOf(`</${m[1]}>`, end);
        if (close > 0) words = literalWords(s.slice(end + 1, close));
      }
      const label = actionLabel(attrs, words);
      if (!label) continue;
      const key = `${surfaceKey}#${m[1]}:${label.length > 70 ? label.slice(0, 70) : label}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ kind: 'uiaction', key, label: `${where} ${m[1]} ${label}`, file: rel(root, file), line: lineAt(s, m.index) });
    }
  }
  return out;
}

// ----------------------------------------------------------------------------------------- hosted services, jobs, checks

export function serviceItems(root) {
  const found = new Map();
  const dirs = ['src', 'apps/windows-tray'];
  for (const dir of dirs) {
    for (const file of csFiles(root, dir)) {
      const s = readCs(file);
      for (const m of s.matchAll(/AddHostedService<\s*(\w+)\s*>/g)) if (!found.has(m[1])) found.set(m[1], rel(root, file));
      for (const m of s.matchAll(/\bclass\s+(\w+)[^{;]*?:\s*[^{;]*?\b(?:BackgroundService|IHostedService)\b/g)) found.set(m[1], rel(root, file));
    }
  }
  // Registered by AddHostedService, the file that declares the class is the one to point at.
  for (const dir of dirs) {
    for (const file of csFiles(root, dir)) {
      const s = readCs(file);
      for (const name of found.keys()) if (new RegExp(`\\b(?:class|record)\\s+${name}\\b`).test(s)) found.set(name, rel(root, file));
    }
  }
  return [...found].sort((a, b) => a[0].localeCompare(b[0])).map(([name, file]) => ({ kind: 'service', key: `service:${name}`, label: name, file }));
}

export function laneItems(root) {
  const file = join(root, 'src/Deluno.Worker/Services/JobLanes.cs');
  if (!existsSync(file)) return [];
  const s = readCs(file);
  const all = s.slice(s.indexOf('IReadOnlyList<JobLane> All'));
  const out = [];
  for (const m of all.matchAll(/^\s*new\(\s*"([\w.-]+)"/gm)) out.push({ kind: 'lane', key: `lane:${m[1]}`, label: m[1], file: rel(root, file) });
  return out;
}

export function handlerItems(root) {
  const out = [];
  for (const file of csFiles(root, 'src/Deluno.Worker')) {
    const s = readCs(file);
    for (const m of s.matchAll(/\bclass\s+(\w+)[^{;]*?:\s*[^{;]*?\bIJobHandler\b/g)) out.push({ kind: 'handler', key: `handler:${m[1]}`, label: m[1], file: rel(root, file) });
  }
  return out.sort((a, b) => a.key.localeCompare(b.key));
}

export function taskItems(root) {
  const file = join(root, 'src/Deluno.Contracts/SystemTasks.cs');
  if (!existsSync(file)) return [];
  const s = readCs(file);
  const consts = new Map([...s.matchAll(/public const string (\w+)\s*=\s*"([^"]+)"/g)].map((m) => [m[1], m[2]]));
  const all = s.slice(s.indexOf('IReadOnlyList<SystemTask> All'));
  const out = [];
  for (const m of all.matchAll(/^\s*new\(\s*(\w+|"[^"]+")/gm)) {
    const key = m[1].startsWith('"') ? m[1].slice(1, -1) : consts.get(m[1]);
    if (key) out.push({ kind: 'task', key: `task:${key}`, label: key, file: rel(root, file) });
  }
  return out;
}

export function healthCheckItems(root) {
  const out = [];
  for (const file of csFiles(root, 'src')) {
    const s = readCs(file);
    for (const m of s.matchAll(/\bclass\s+(\w+)[^{;]*?:\s*[^{;]*?\bIHealthCheck\b/g)) out.push({ kind: 'healthcheck', key: `healthcheck:${m[1]}`, label: m[1], file: rel(root, file) });
  }
  return out.sort((a, b) => a.key.localeCompare(b.key));
}

// ------------------------------------------------------------------------------------------------------------ migrations

export function migrationItems(root) {
  const out = [];
  for (const file of csFiles(root, 'src')) {
    const name = basename(file);
    if (!/[\\/]Migrations[\\/]/.test(file) || /Infrastructure[\\/]Storage[\\/]Migrations/.test(file)) continue;
    const project = /src[\\/]Deluno\.(\w+)[\\/]/.exec(file)?.[1]?.toLowerCase() ?? 'unknown';
    const m = /^V(\d{4,})(\w*)\.cs$/.exec(name);
    if (m) out.push({ kind: 'migration', key: `migration:${project}/V${m[1]}`, label: `${project} V${m[1]} ${m[2]}`, file: rel(root, file) });
    else if (/Migration\.cs$/.test(name)) out.push({ kind: 'migration', key: `migration:${project}/${name.slice(0, -3)}`, label: `${project} ${name.slice(0, -3)}`, file: rel(root, file) });
  }
  return out.sort((a, b) => a.key.localeCompare(b.key));
}

// ---------------------------------------------------------------------------------------------------------- integrations
//
// An integration client is a class that talks to another app or service. FAMILIES lists them with the file that holds the class;
// the operations of a client are the public methods that return a Task (and, for a download client, every method of IDownloadClient).
// A source file that uses an HttpClient and is in neither FAMILIES nor IGNORED is reported as an unclassified outbound caller,
// which is an inventory item too: a new client cannot arrive without a decision.

const DC = 'src/Deluno.Integrations/DownloadClients/Clients/';
export const FAMILIES = [
  { id: 'qbittorrent', file: DC + 'QbittorrentDownloadClient.cs', downloadClient: true },
  { id: 'transmission', file: DC + 'TransmissionDownloadClient.cs', downloadClient: true },
  { id: 'deluge', file: DC + 'DelugeDownloadClient.cs', downloadClient: true },
  { id: 'sabnzbd', file: DC + 'SabnzbdDownloadClient.cs', downloadClient: true },
  { id: 'nzbget', file: DC + 'NzbGetDownloadClient.cs', downloadClient: true },
  { id: 'utorrent', file: DC + 'UTorrentDownloadClient.cs', downloadClient: true },
  { id: 'rtorrent', file: DC + 'RtorrentDownloadClient.cs', downloadClient: true },
  { id: 'weir', file: 'src/Deluno.Integrations/Processors/ProcessorConnectionService.cs' },
  { id: 'plex', file: 'src/Deluno.Integrations/MediaServers/PlexMediaServerClient.cs' },
  { id: 'jellyfin-emby', file: 'src/Deluno.Integrations/MediaServers/EmbyFamilyMediaServerClient.cs' },
  { id: 'media-server-http', file: 'src/Deluno.Integrations/MediaServers/MediaServerHttp.cs' },
  { id: 'seerr', file: 'src/Deluno.Integrations/Seerr/SeerrClient.cs' },
  { id: 'trakt', file: 'src/Deluno.Integrations/Trakt/TraktClient.cs' },
  { id: 'tmdb', file: 'src/Deluno.Integrations/Metadata/TmdbMetadataProvider.cs' },
  { id: 'tvdb', file: 'src/Deluno.Integrations/Metadata/TvdbDirectClient.cs' },
  { id: 'xem', file: 'src/Deluno.Integrations/Metadata/SceneMapping/XemSceneMappingProvider.cs' },
  { id: 'metadata-broker-health', file: 'src/Deluno.Integrations/Metadata/MetadataServiceHealth.cs' },
  { id: 'indexer-feed', file: 'src/Deluno.Integrations/Search/IndexerFeedReader.cs' },
  { id: 'indexer-search-planner', file: 'src/Deluno.Integrations/Search/FeedMediaSearchPlanner.cs' },
  { id: 'indexer-torrent-fetch', file: 'src/Deluno.Integrations/DownloadClients/IndexerTorrentFetcher.cs' },
  { id: 'tracker-engine', file: 'src/Deluno.Integrations/Search/Trackers/TrackerEngine.cs' },
  { id: 'tracker-definitions-github', file: 'src/Deluno.Integrations/Search/Trackers/GitHubTrackerDefinitionSource.cs' },
  { id: 'cloudflare-helper', file: 'src/Deluno.Integrations/Search/Trackers/CloudflareHelper.cs' },
  { id: 'indexer-proxy', file: 'src/Deluno.Connections/Data/IndexerProxyHttpClientFactory.cs' },
  { id: 'indexer-aggregator-names', file: 'src/Deluno.Connections/Data/AggregatorIndexerNames.cs' },
  { id: 'qbittorrent-sign-in', file: 'src/Deluno.Connections/QbittorrentSignIn.cs' },
  { id: 'github-latest-release', file: 'src/Deluno.Connections/Versions/LatestReleaseService.cs' },
  { id: 'trash-guide-upstream', file: 'src/Deluno.Quality/Guides/GuideUpstreamTreeClient.cs' },
  { id: 'notifications', file: 'src/Deluno.Notifications/OutboundNotificationService.cs' },
  { id: 'list-intake', file: 'src/Deluno.Worker/Intake/IntakeSyncService.cs' },
  { id: 'content-blocklist-remote', file: 'src/Deluno.Worker/Services/ContentBlocklistRemoteRefreshService.cs' },
  { id: 'companion-tools', file: 'src/Deluno.Companions/CompanionTools.cs' },
  { id: 'companion-downloads', file: 'src/Deluno.Companions/CompanionDownloads.cs' },
  { id: 'proxy-reachability', file: 'src/Deluno.Worker/HealthChecks/Checks/HttpProxyReachabilityTester.cs' },
  { id: 'system-time-check', file: 'src/Deluno.Worker/HealthChecks/Checks/SystemTimeHealthCheck.cs' },
  { id: 'cloudflare-helper-check', file: 'src/Deluno.Worker/HealthChecks/Checks/CloudflareHelperHealthCheck.cs' },
  { id: 'network-checker', file: 'src/Deluno.Infrastructure/SystemReadings/NetworkChecker.cs' },
  { id: 'download-health-connectivity', file: 'src/Deluno.Integrations/DownloadClients/DownloadHealthConnectivityCheck.cs' },
  { id: 'subtitle-http', file: 'src/Deluno.Integrations/Subtitles/SubtitleProviderHttp.cs' },
  { id: 'tray-github-api', file: 'apps/windows-tray/GithubApiClient.cs' },
  { id: 'tray-github-latest', file: 'apps/windows-tray/GithubLatestRelease.cs' },
  { id: 'tray-directory-latest', file: 'apps/windows-tray/DirectoryListingLatestRelease.cs' },
  { id: 'tray-release-feed', file: 'apps/windows-tray/ReleaseFeedUpdateSource.cs' },
  { id: 'tray-component-install', file: 'apps/windows-tray/ComponentInstallService.cs' },
  { id: 'tray-download-client-setup', file: 'apps/windows-tray/DownloadClientSetup.cs' },
  { id: 'tray-component-data-folder', file: 'apps/windows-tray/ComponentDataFolder.cs' }
];

// Outbound HTTP users that are plumbing, not an integration: registration, logging, shared helpers, the endpoints that call a client.
const IGNORED_HTTP_USERS = [
  /ServiceCollectionExtensions\.cs$/, /EndpointRouteBuilderExtensions\.cs$/, /Endpoints\.cs$/, /DelunoFileLogger\.cs$/,
  /IntegrationFailures\.cs$/, /ReleaseFallback\.cs$/, /SizedJson\.cs$/, /HealthCheckHttpClients\.cs$/, /DownloadClientHelpers\.cs$/,
  /MediaServerHttp\.cs$/ /* listed as a family */
];

function operationsOf(text) {
  const names = new Set();
  const re = /^[ \t]*(public|internal|protected)\s+(?:static\s+)?(?:sealed\s+)?(?:async\s+)?(?:override\s+)?(?:new\s+)?(Task|ValueTask)\b/gm;
  let m;
  while ((m = re.exec(text))) {
    let i = m.index + m[0].length;
    if (text[i] === '<') {
      let depth = 0;
      for (; i < text.length; i++) {
        if (text[i] === '<') depth++;
        else if (text[i] === '>') { depth--; if (depth === 0) { i++; break; } }
      }
    }
    const rest = /^\s+(\w+)\s*(?:<[^>]*>)?\s*\(/.exec(text.slice(i, i + 200));
    if (rest && m[1] !== 'protected') names.add(rest[1]);
    else if (rest && m[1] === 'protected' && /Async$/.test(rest[1])) names.add(rest[1]);
  }
  return names;
}

function interfaceOperations(root, file) {
  const p = join(root, file);
  if (!existsSync(p)) return new Set();
  const text = readCs(p);
  const names = new Set();
  for (const m of text.matchAll(/^\s*(?:Task|ValueTask)(?:<[^\n]*?>)?\s+(\w+)\s*\(/gm)) names.add(m[1]);
  return names;
}

export function integrationItems(root) {
  const out = [];
  const known = new Set();
  const dcOps = interfaceOperations(root, 'src/Deluno.Integrations/DownloadClients/IDownloadClient.cs');
  for (const fam of FAMILIES) {
    known.add(fam.file);
    const p = join(root, fam.file);
    if (!existsSync(p)) {
      continue;
    }
    out.push({ kind: 'integration', key: `integration:${fam.id}`, label: fam.id, file: fam.file, client: true });
    const ops = operationsOf(readCs(p));
    if (fam.downloadClient) for (const op of dcOps) ops.add(op);
    for (const op of [...ops].sort()) out.push({ kind: 'integration', key: `integration:${fam.id}.${op}`, label: `${fam.id} ${op}`, file: fam.file, operation: op, client: fam.id });
  }
  // Subtitle providers: one family each, with the operations the shared ISubtitleProvider asks of them.
  const subDir = 'src/Deluno.Integrations/Subtitles/Providers';
  const providerOps = interfaceOperations(root, 'src/Deluno.Integrations/Subtitles/ISubtitleProvider.cs');
  for (const file of csFiles(root, subDir)) {
    const f = rel(root, file);
    known.add(f);
    const id = 'subtitles-' + basename(file).replace(/SubtitleProvider\.cs$/, '').toLowerCase();
    out.push({ kind: 'integration', key: `integration:${id}`, label: id, file: f, client: true });
    const ops = operationsOf(readCs(file));
    for (const op of providerOps) ops.add(op);
    for (const op of [...ops].sort()) out.push({ kind: 'integration', key: `integration:${id}.${op}`, label: `${id} ${op}`, file: f, operation: op, client: id });
  }
  // Anything else that uses an HttpClient is a client nobody has classified.
  const tray = csFiles(root, 'apps/windows-tray');
  for (const file of [...csFiles(root, 'src'), ...tray]) {
    const f = rel(root, file);
    if (known.has(f) || IGNORED_HTTP_USERS.some((re) => re.test(f))) continue;
    if (!/\b(HttpClient|IHttpClientFactory)\b/.test(readCs(file))) continue;
    out.push({ kind: 'integration', key: `integration:unclassified:${f}`, label: `unclassified outbound caller ${f}`, file: f, unclassified: true });
  }
  return out;
}

// ------------------------------------------------------------------------------------------------------------------ tray

export function trayItems(root) {
  const file = join(root, 'apps/windows-tray/TrayModel.cs');
  if (!existsSync(file)) return [];
  const s = readCs(file);
  const m = /enum\s+TrayMenuId\s*\{([^}]*)\}/.exec(s);
  if (!m) return [];
  return m[1].split(',').map((x) => x.replace(/\/\/.*$/gm, '').trim()).filter((x) => x && x !== 'Separator')
    .map((name) => ({ kind: 'tray', key: `tray:${name}`, label: name, file: 'apps/windows-tray/TrayModel.cs' }));
}

// ------------------------------------------------------------------------------------------------------------- scenarios

export function scenarioPlan(root) {
  const planFile = join(root, 'scripts/golden-path/suite/ScenarioPlan.ps1');
  if (!existsSync(planFile)) return { ids: [], groups: [], error: null };
  const script = `. '${planFile.replace(/'/g, "''")}'; Get-ScenarioPlan | ForEach-Object { $g = $_; foreach ($i in $g.Ids) { "$($g.Name)|$($g.Function)|$i" } }`;
  for (const shell of ['pwsh', 'powershell']) {
    const encoded = Buffer.from(script, 'utf16le').toString('base64');
    const r = spawnSync(shell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded], { encoding: 'utf8', windowsHide: true });
    if (r.error || r.status !== 0) continue;
    const groups = new Map();
    const ids = [];
    for (const line of r.stdout.split(/\r?\n/)) {
      const [group, fn, id] = line.trim().split('|');
      if (!id) continue;
      ids.push({ group, fn, id });
    }
    return { ids, error: null };
  }
  return { ids: [], error: 'no PowerShell found to read ScenarioPlan.ps1' };
}

export function scenarioItems(root) {
  const plan = scenarioPlan(root);
  return plan.ids.map(({ id, group, fn }) => ({ kind: 'scenario', key: `scenario:${id}`, label: id, file: 'scripts/golden-path/suite/ScenarioPlan.ps1', group, fn }));
}

// --------------------------------------------------------------------------------------------------------------- everything

export function buildInventory(root, { skipScenarios = false } = {}) {
  const items = [
    ...routeItems(root), ...webRouteItems(root), ...uiItems(root), ...serviceItems(root), ...laneItems(root), ...handlerItems(root), ...taskItems(root),
    ...healthCheckItems(root), ...migrationItems(root), ...integrationItems(root), ...trayItems(root),
    ...(skipScenarios ? [] : scenarioItems(root))
  ];
  const seen = new Set();
  return items.filter((i) => (seen.has(i.key) ? false : (seen.add(i.key), true)));
}

export function countsOf(items) {
  const counts = {};
  for (const i of items) counts[i.kind] = (counts[i.kind] ?? 0) + 1;
  return counts;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
  const items = buildInventory(root, { skipScenarios: process.argv.includes('--no-scenarios') });
  if (process.argv.includes('--json')) console.log(JSON.stringify(items, null, 1));
  else if (process.argv.includes('--list')) for (const i of items) console.log(`${i.kind.padEnd(12)} ${i.key}  ${i.file ?? ''}`);
  else {
    const counts = countsOf(items);
    for (const [kind, n] of Object.entries(counts)) console.log(`${kind.padEnd(12)} ${n}`);
    console.log(`${'total'.padEnd(12)} ${items.length}`);
  }
}
