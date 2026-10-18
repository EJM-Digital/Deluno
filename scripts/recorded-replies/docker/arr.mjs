// Records the real Sonarr, Radarr and Prowlarr replies Deluno's migration assistant reads, from their published images in Docker (#1237).
//
//   node scripts/recorded-replies/docker/arr.mjs sonarr radarr prowlarr
//
// The migration assistant takes what a person copies out of these apps: the series and movie lists, the quality profiles, the tags, the
// root folders, the custom formats, the download clients and the indexers (Prowlarr's indexer list is its export). Each app is set up the
// way a person would, through its own API, with a real title looked up on the public metadata service, a root folder, a tag, a custom
// format, qBittorrent as its download client and the stack's Torznab feed as its indexer. Their API keys are read from each app's
// config.xml inside its container and are removed from the recordings.
import { execFileSync } from 'node:child_process';
import { Client, addSecret, local, replyJson, save, until } from './lib.mjs';

const project = process.env.REC_PROJECT ?? 'recdocker';
const docker = (...args) => { const parts = (process.env.REC_DOCKER ?? 'wsl -e docker').split(' ').filter(Boolean); return execFileSync(parts[0], [...parts.slice(1), ...args], { encoding: 'utf8', timeout: 120_000 }); };
const apiKeyOf = (app) => docker('exec', `${project}-${app}-1`, 'sh', '-c', 'grep -o "<ApiKey>[^<]*" /config/config.xml | cut -c9-').trim();

async function connect(app, apiVersion) {
  const key = apiKeyOf(app);
  addSecret(key);
  const c = new Client(local(app), { headers: { 'X-Api-Key': key } });
  const get = (p) => c.get(`/api/${apiVersion}/${p}`);
  const post = async (p, json) => {
    const e = await c.send({ method: 'POST', path: `/api/${apiVersion}/${p}`, json });
    if (e.reply.status >= 300) throw new Error(`${app} POST ${p} answered ${e.reply.status}: ${e.reply.bodyText.slice(0, 600)}`);
    return e;
  };
  await until(`${app} to answer`, 180, async () => (await get('system/status')).reply.status === 200, 3000);
  const version = replyJson(await get('system/status')).version;
  // Makes what is not there yet; a second run finds what the first made (the exchange of the creating call is then the listing's).
  const once = async (path, exists, body) => {
    const listing = await get(path.split('?')[0]);
    return (replyJson(listing) ?? []).some(exists) ? listing : await post(path, body);
  };
  return { c, get, post, once, version };
}

/** The settings both Sonarr and Radarr take: a tag, qBittorrent as a download client, the stack's feed as an indexer, a custom format. */
async function commonSettings(a, { tvOrMovie }) {
  await a.once('tag', (t) => t.label === 'recorded', { label: 'recorded' });
  const tag = replyJson(await a.get('tag')).find((t) => t.label === 'recorded');
  const customFormat = await a.once('customformat', (c) => c.name === 'Recorded x265', {
    name: 'Recorded x265', includeCustomFormatWhenRenaming: false,
    specifications: [{ name: 'x265', implementation: 'ReleaseTitleSpecification', negate: false, required: true, fields: [{ name: 'value', value: '(x|h)\\.?265' }] }]
  });
  const clientSchema = replyJson(await a.get('downloadclient/schema')).find((s) => s.implementation === 'QBittorrent');
  const withFields = (schema, values) => ({ ...schema, fields: schema.fields.map((f) => (f.name in values ? { ...f, value: values[f.name] } : f)) });
  const client = await a.once('downloadclient', (c) => c.name === 'qBittorrent', {
    ...withFields(clientSchema, { host: 'qbittorrent', port: 8080, username: 'e2e', password: 'e2e-qbittorrent-pass', [tvOrMovie === 'tv' ? 'tvCategory' : 'movieCategory']: 'recorded', useSsl: false }),
    name: 'qBittorrent', enable: true, priority: 1, tags: []
  });
  // The stack's feed answers movie searches only (tests/e2e/fixture/server.mjs ignores TV searches), and Sonarr tests an indexer with a TV search, so
  // Sonarr gets no indexer; Radarr's and Prowlarr's indexer lists hold the feed.
  let indexer = null;
  if (tvOrMovie === 'movie') {
    const indexerSchema = replyJson(await a.get('indexer/schema')).find((s) => s.implementation === 'Torznab' && !s.presets?.length) ?? replyJson(await a.get('indexer/schema')).find((s) => s.implementation === 'Torznab');
    indexer = await a.once('indexer?forceSave=true', (i) => i.name === 'E2E feed', {
      ...withFields(indexerSchema, { baseUrl: 'http://fixture:8765', apiPath: '/api', apiKey: 'e2e', categories: [2000] }),
      name: 'E2E feed', enableRss: true, enableAutomaticSearch: true, enableInteractiveSearch: true, priority: 25, tags: [], protocol: 'torrent'
    });
  }
  return { tag, customFormat, client, indexer };
}

const note = (what, version) => `${what} (${version}, a fresh container set up through its own API with a public title, a tag, a custom format, qBittorrent and, for Radarr, a Torznab feed).`;

async function sonarr() {
  const a = await connect('sonarr', 'v3');
  docker('exec', '--user', 'root', `${project}-sonarr-1`, 'sh', '-c', 'mkdir -p /tv && chown 1654:1654 /tv');
  const root = await a.once('rootfolder', (r) => r.path === '/tv', { path: '/tv' });
  const profiles = replyJson(await a.get('qualityprofile'));
  const settings = await commonSettings(a, { tvOrMovie: 'tv' });
  const tagId = settings.tag.id;
  // A real public-domain series, looked up on Sonarr's own metadata service.
  const found = replyJson(await a.get('series/lookup?term=' + encodeURIComponent('Pioneer One')));
  const pick = found.find((s) => /pioneer one/i.test(s.title)) ?? found[0];
  const added = await a.once('series', (x) => x.title === pick.title, { ...pick, qualityProfileId: profiles[0].id, rootFolderPath: '/tv', monitored: false, seasonFolder: true, tags: [tagId], addOptions: { monitor: 'none', searchForMissingEpisodes: false, searchForCutoffUnmetEpisodes: false } });
  const v = a.version;
  save('sonarr', v, 'system-status', note('GET /api/v3/system/status', v), [await a.get('system/status')]);
  save('sonarr', v, 'series', note('GET /api/v3/series: one real series', v), [added, await a.get('series')]);
  save('sonarr', v, 'quality-profiles', note('GET /api/v3/qualityprofile', v), [await a.get('qualityprofile')]);
  save('sonarr', v, 'tags', note('GET /api/v3/tag', v), [await a.get('tag')]);
  save('sonarr', v, 'root-folders', note('GET /api/v3/rootfolder', v), [root, await a.get('rootfolder')]);
  save('sonarr', v, 'custom-formats', note('GET /api/v3/customformat', v), [await a.get('customformat')]);
  save('sonarr', v, 'download-clients', note('GET /api/v3/downloadclient', v), [await a.get('downloadclient')]);
  save('sonarr', v, 'indexers', note('GET /api/v3/indexer', v), [await a.get('indexer')]);
}

async function radarr() {
  const a = await connect('radarr', 'v3');
  docker('exec', '--user', 'root', `${project}-radarr-1`, 'sh', '-c', 'mkdir -p /movies && chown 1654:1654 /movies');
  const root = await a.once('rootfolder', (r) => r.path === '/movies', { path: '/movies' });
  const profiles = replyJson(await a.get('qualityprofile'));
  const settings = await commonSettings(a, { tvOrMovie: 'movie' });
  const tagId = settings.tag.id;
  // Big Buck Bunny, a real open film, looked up by its TMDb id on Radarr's own metadata service.
  const found = replyJson(await a.get('movie/lookup/tmdb?tmdbId=10378'));
  const added = await a.once('movie', (x) => x.tmdbId === 10378, { ...found, qualityProfileId: profiles[0].id, rootFolderPath: '/movies', monitored: false, minimumAvailability: 'released', tags: [tagId], addOptions: { monitor: 'none', searchForMovie: false } });
  const v = a.version;
  save('radarr', v, 'system-status', note('GET /api/v3/system/status', v), [await a.get('system/status')]);
  save('radarr', v, 'movies', note('GET /api/v3/movie: one real film', v), [added, await a.get('movie')]);
  save('radarr', v, 'quality-profiles', note('GET /api/v3/qualityprofile', v), [await a.get('qualityprofile')]);
  save('radarr', v, 'tags', note('GET /api/v3/tag', v), [await a.get('tag')]);
  save('radarr', v, 'root-folders', note('GET /api/v3/rootfolder', v), [root, await a.get('rootfolder')]);
  save('radarr', v, 'custom-formats', note('GET /api/v3/customformat', v), [await a.get('customformat')]);
  save('radarr', v, 'download-clients', note('GET /api/v3/downloadclient', v), [await a.get('downloadclient')]);
  save('radarr', v, 'indexers', note('GET /api/v3/indexer', v), [await a.get('indexer')]);
}

async function prowlarr() {
  const a = await connect('prowlarr', 'v1');
  const withFields = (schema, values) => ({ ...schema, fields: schema.fields.map((f) => (f.name in values ? { ...f, value: values[f.name] } : f)) });
  await a.once('tag', (t) => t.label === 'recorded', { label: 'recorded' });
  const schema = replyJson(await a.get('indexer/schema')).find((s) => s.implementation === 'Torznab' && !s.presets?.length) ?? replyJson(await a.get('indexer/schema')).find((s) => s.implementation === 'Torznab');
  const profiles = replyJson(await a.get('appprofile'));
  const added = await a.once('indexer?forceSave=true', (i) => i.name === 'E2E feed', {
    ...withFields(schema, { baseUrl: 'http://fixture:8765', apiPath: '/api', apiKey: 'e2e' }),
    name: 'E2E feed', enable: true, redirect: false, appProfileId: profiles[0].id, priority: 25, tags: [], protocol: 'torrent'
  });
  const v = a.version;
  save('prowlarr', v, 'system-status', `GET /api/v1/system/status (${v}).`, [await a.get('system/status')]);
  save('prowlarr', v, 'indexers', `GET /api/v1/indexer (${v}), Prowlarr's indexer list that a person copies out: the stack's Torznab feed added through its own API.`, [added, await a.get('indexer')]);
  save('prowlarr', v, 'tags', `GET /api/v1/tag (${v}).`, [await a.get('tag')]);
  save('prowlarr', v, 'download-clients', `GET /api/v1/downloadclient (${v}): none are set up.`, [await a.get('downloadclient')]);
}

const jobs = { sonarr, radarr, prowlarr };
for (const name of process.argv.slice(2)) {
  if (!jobs[name]) { console.error('unknown: ' + name); process.exit(2); }
  await jobs[name]();
}
console.log('done');
