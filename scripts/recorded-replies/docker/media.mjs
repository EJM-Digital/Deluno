// Records the real Jellyfin, Emby and Plex Media Server replies Deluno's media server clients read, from their published images in Docker (#1237).
//
//   node scripts/recorded-replies/docker/media.mjs jellyfin emby plex
//
// Each server is given a small real library (one film, and a two-episode series made from the same generated video, found by name so the server
// fetches its details from the internet) and driven the way the clients drive it: the setup wizard, an API key, the catalogue with provider ids,
// watched, a play in progress, a library refresh, the folder browser, collections, an item delete and a user made. These are the states
// scripts/recorded-replies/Record-MediaServers.ps1 lists; this port runs them in containers instead of the golden VM.
//
// Plex is recorded UNCLAIMED: no account is made, so every call is local and carries no token. What needs plex.tv (the user list, shared
// servers, resources, a token that names an owner) stays unrecorded and is listed in tests/fixtures/recorded/MISSING.md.
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { Client, addSecret, addMissing, replyJson, save as saveState, sleep, until } from './lib.mjs';

const project = process.env.REC_PROJECT2 ?? 'rec2';
const dockerParts = (process.env.REC_DOCKER ?? 'wsl -e docker').split(' ').filter(Boolean);
const docker = (...args) => execFileSync(dockerParts[0], [...dockerParts.slice(1), ...args], { encoding: 'utf8', timeout: 240_000 });
const PORTS = { jellyfin: Number(process.env.REC2_JELLYFIN_PORT ?? 38096), emby: Number(process.env.REC2_EMBY_PORT ?? 38097), plex: Number(process.env.REC2_PLEX_PORT ?? 32400) };
const CLIENT_AUTH = 'Client="Deluno Recorder", Device="Recorder", DeviceId="deluno-recorder-1", Version="1.0"';
const FIELDS = 'ProviderIds,Path,DateCreated,ProductionYear,PremiereDate,Genres,People,Tags,TagItems,CommunityRating,CriticRating,MediaSources,SeriesId,SeasonId,ParentIndexNumber,IndexNumber';
const MOVIE_DIR = '/media/Movies/Sintel (2010)';
const MOVIE_FILE = `${MOVIE_DIR}/Sintel (2010) [WEB 1080p].mkv`;
const SHOW_DIR = '/media/Shows/Recorded Show (2020)/Season 01';

/** One generated video (a few seconds, small) put in the shared media volume as the film and as two episodes. */
function makeMedia() {
  const volume = `${project}_media`;
  const script = [
    'set -e', `mkdir -p "${MOVIE_DIR}" "${SHOW_DIR}"`,
    `if [ ! -f "${MOVIE_FILE}" ]; then /usr/lib/jellyfin-ffmpeg/ffmpeg -y -loglevel error -f lavfi -i testsrc=size=640x360:rate=24 -f lavfi -i sine=frequency=440 -t 20 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac "${MOVIE_FILE}"; fi`,
    `for e in 1 2; do [ -f "${SHOW_DIR}/Recorded Show (2020) - S01E0$e.mkv" ] || cp "${MOVIE_FILE}" "${SHOW_DIR}/Recorded Show (2020) - S01E0$e.mkv"; done`,
    'chmod -R a+rwX /media'
  ].join('; ');
  docker('run', '--rm', '-v', `${volume}:/media`, '--entrypoint', 'sh', 'jellyfin/jellyfin:latest', '-c', script);
}

// REC_ONLY=state,state limits what is written (to add states to an app already recorded without replacing its other files).
const only = process.env.REC_ONLY ? new Set(process.env.REC_ONLY.split(',')) : null;
const save = (app, version, state, ...rest) => { if (!only || only.has(state)) saveState(app, version, state, ...rest); };
const mediaNote = (kind, version, what) => `${what} (${kind} ${version} in Docker, a library of one film and a two-episode series made from one generated video, found by name).`;

// ------------------------------------------------------------------ Jellyfin and Emby (the same API; Emby lives under /emby)

async function embyFamily(kind) {
  const emby = kind === 'emby';
  const prefix = emby ? '/emby' : '';
  const userHeader = emby ? 'X-Emby-Authorization' : 'Authorization';
  const base = `http://127.0.0.1:${PORTS[kind]}`;
  const anon = new Client(base, { keep: ['content-type'] });
  const pub = await until(`${kind} to answer`, 240, async () => { const e = await anon.get(`${prefix}/System/Info/Public`); return e.reply.status === 200 && replyJson(e)?.Version ? e : null; }, 3000);
  await sleep(8000);
  const version = String(replyJson(pub).Version);
  console.log(`${kind} ${version}`);
  const note = (what) => mediaNote(kind, version, what);

  // The first-run wizard, step by step.
  const name = 'rec' + randomBytes(3).toString('hex');
  const pass = randomBytes(9).toString('base64url');
  addSecret(name); addSecret(pass);
  const state = await anon.get(`${prefix}/System/Info/Public`);
  const wizard = [];
  if (replyJson(state).StartupWizardCompleted !== true) {
    wizard.push(await anon.get(`${prefix}/Startup/Configuration`));
    wizard.push(await anon.send({ method: 'POST', path: `${prefix}/Startup/Configuration`, json: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' } }));
    wizard.push(await anon.get(`${prefix}/Startup/User`));
    wizard.push(await anon.send({ method: 'POST', path: `${prefix}/Startup/User`, json: { Name: name, Password: pass } }));
    wizard.push(await anon.send({ method: 'POST', path: `${prefix}/Startup/RemoteAccess`, json: { EnableRemoteAccess: false, EnableAutomaticPortMapping: false } }));
    wizard.push(await anon.send({ method: 'POST', path: `${prefix}/Startup/Complete` }));
  } else throw new Error(`${kind} is already set up; start it from a fresh volume`);
  const authenticate = async () => {
    const e = await anon.send({ method: 'POST', path: `${prefix}/Users/AuthenticateByName`, headers: { [userHeader]: `MediaBrowser ${CLIENT_AUTH}` }, json: { Username: name, Pw: pass } });
    const j = replyJson(e);
    if (j?.AccessToken) addSecret(j.AccessToken);
    return { exchange: e, token: j?.AccessToken, userId: j?.User?.Id };
  };
  const login = await until('the administrator sign-in', 60, async () => { const l = await authenticate(); return l.token ? l : null; }, 3000);
  const adminHeader = { [userHeader]: `MediaBrowser ${CLIENT_AUTH}, Token="${login.token}"` };
  const mk = await anon.send({ method: 'POST', path: `${prefix}/Auth/Keys?app=Deluno`, headers: adminHeader });
  const keys = await anon.send({ method: 'GET', path: `${prefix}/Auth/Keys`, headers: adminHeader });
  const key = (replyJson(keys)?.Items ?? []).find((i) => i.AppName === 'Deluno')?.AccessToken;
  if (!key) { addMissing(`${kind}: no API key could be made (POST /Auth/Keys answered ${mk.reply.status})`); return; }
  addSecret(key);
  const keyHeaders = emby ? { 'X-Emby-Token': key } : { Authorization: `MediaBrowser Token="${key}"` };
  const badHeaders = emby ? { 'X-Emby-Token': 'not-the-key' } : { Authorization: 'MediaBrowser Token="not-the-key"' };
  const c = new Client(base, { keep: ['content-type'], headers: { accept: 'application/json' } });
  const get = (path) => c.send({ method: 'GET', path: prefix + path, headers: keyHeaders });
  const send = (method, path, json) => c.send({ method, path: prefix + path, headers: keyHeaders, json });

  save(kind, version, 'wizard', note('The first-run setup wizard, step by step, and the administrator\'s sign-in (the login and its token are removed)'), [pub, state, ...wizard, login.exchange, mk, keys]);
  save(kind, version, 'identity', note('The public identity and the full system info Deluno reads for the version and server id'), [pub, await get('/System/Info')]);
  save(kind, version, 'wrong-token', note('System info, the user list and the library list with a key the server does not know'), [
    await c.send({ path: `${prefix}/System/Info`, headers: badHeaders }), await c.send({ path: `${prefix}/Users`, headers: badHeaders }), await c.send({ path: `${prefix}/Library/VirtualFolders`, headers: badHeaders })]);
  save(kind, version, 'no-token', note('System info with no key at all'), [await c.get(`${prefix}/System/Info`)]);
  save(kind, version, 'library-empty', note('Before any library exists: the virtual folders, the users and an empty catalogue'), [
    await get('/Library/VirtualFolders'), await get('/Users'), await get(`/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&Fields=${FIELDS}&StartIndex=0&Limit=500`), await get('/Sessions')]);

  // Libraries over the two folders; the server scans them and fetches the details from the internet.
  // Emby makes a library with no metadata fetchers unless it is told which (the web app sends every one it offers); Jellyfin fills them in itself.
  const optionsFor = async (type) => {
    if (!emby) return {};
    const available = replyJson(await get(`/Libraries/AvailableOptions?LibraryContentType=${type}`));
    const typeOptions = (available?.TypeOptions ?? []).map((t) => ({ Type: t.Type, MetadataFetchers: (t.MetadataFetchers ?? []).map((f) => f.Name), MetadataFetcherOrder: (t.MetadataFetchers ?? []).map((f) => f.Name), ImageFetchers: (t.ImageFetchers ?? []).map((f) => f.Name), ImageFetcherOrder: (t.ImageFetchers ?? []).map((f) => f.Name) }));
    const metadataSavers = (available?.MetadataSavers ?? []).map((m) => m.Name);
    return { TypeOptions: typeOptions, EnableRealtimeMonitor: true, SaveLocalMetadata: false, MetadataSavers: [], PreferredMetadataLanguage: 'en', MetadataCountryCode: 'US', EnableInternetProviders: true, SkipSubtitlesIfAudioTrackMatches: false, metadataSavers };
  };
  const mkMovies = await send('POST', `/Library/VirtualFolders?name=Movies&collectionType=movies&paths=${encodeURIComponent('/media/Movies')}&refreshLibrary=true`, { LibraryOptions: await optionsFor('movies') });
  console.log('  movies library: ' + mkMovies.reply.status);
  await sleep(10000);
  const mkShows = await send('POST', `/Library/VirtualFolders?name=Shows&collectionType=tvshows&paths=${encodeURIComponent('/media/Shows')}&refreshLibrary=true`, { LibraryOptions: await optionsFor('tvshows') });
  console.log('  shows library: ' + mkShows.reply.status + ' ' + (mkShows.reply.bodyText ?? '').slice(0, 200));
  save(kind, version, 'libraries-created', note('A movies library and a TV library made over the two folders'), [mkMovies, mkShows, await get('/Library/VirtualFolders')]);
  await until('the library scan', 300, async () => {
    const m = replyJson(await get('/Items?Recursive=true&IncludeItemTypes=Movie')); const t = replyJson(await get('/Items?Recursive=true&IncludeItemTypes=Episode'));
    console.log(`  scan: ${m?.TotalRecordCount} films, ${t?.TotalRecordCount} episodes`);
    return m?.TotalRecordCount >= 1 && t?.TotalRecordCount >= 2;
  }, 5000);
  // The details (provider ids) arrive from the metadata sources a little after the scan; ask for a refresh once if they are slow.
  let asked = false;
  const started = Date.now();
  await until('the film and the series to carry provider ids', 360, async () => {
    const m = replyJson(await get('/Items?Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds'))?.Items ?? [];
    const t = replyJson(await get('/Items?Recursive=true&IncludeItemTypes=Series&Fields=ProviderIds'))?.Items ?? [];
    const ready = m.length >= 1 && m.every((i) => i.ProviderIds && Object.keys(i.ProviderIds).length) && t.length >= 1 && t.every((i) => i.ProviderIds && Object.keys(i.ProviderIds).length);
    if (!ready && !asked && Date.now() - started > 60000) {
      asked = true;
      for (const i of [...m, ...t]) await send('POST', `/Items/${i.Id}/Refresh?Recursive=true&ImageRefreshMode=FullRefresh&MetadataRefreshMode=FullRefresh&ReplaceAllImages=false&ReplaceAllMetadata=false`);
    }
    return ready;
  }, 5000).catch(() => console.log('  provider ids never arrived; recording without them'));
  const users = replyJson(await get('/Users'));
  const userId = users[0].Id;
  const movies = await get(`/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&Fields=${FIELDS}&StartIndex=0&Limit=500`);
  const series = await get(`/Items?Recursive=true&IncludeItemTypes=Series,Season,Episode&CollapseBoxSetItems=false&Fields=${FIELDS}&StartIndex=0&Limit=500`);
  const movieId = replyJson(movies).Items[0].Id;
  const mediaSourceId = replyJson(movies).Items[0].MediaSources?.[0]?.Id ?? movieId;
  const seriesId = replyJson(series).Items.find((i) => i.Type === 'Series').Id;
  // What each person has done with a title: Deluno asks for the played, the favourite and the resumable ones, one listing each.
  const watchedQueries = async () => {
    const out = [];
    for (const f of ['IsPlayed=true', 'IsFavorite=true', 'Filters=IsResumable']) out.push(await get(`/Users/${userId}/Items?Recursive=true&IncludeItemTypes=Movie&CollapseBoxSetItems=false&${f}&Fields=UserData&StartIndex=0&Limit=500`));
    return out;
  };
  save(kind, version, 'catalogue-movies', note('The movie catalogue as Deluno asks for it (Fields with provider ids, paths, media sources; unpaged first page)'), [movies, ...(await watchedQueries())]);
  save(kind, version, 'catalogue-series', note('The series catalogue: the series, its seasons and episodes'), [series, await get(`/Items?Ids=${seriesId}&Fields=ProviderIds`)]);
  save(kind, version, 'libraries', note('The libraries and users as the connection test lists them'), [await get('/Library/VirtualFolders'), await get('/Users')]);

  // Watched.
  const played = await send('POST', `/Users/${userId}/PlayedItems/${movieId}`);
  save(kind, version, 'watched', note('The film marked watched by the administrator, then the played, favourite and resumable listings as Deluno asks for them'), [played, ...(await watchedQueries())]);
  await send('DELETE', `/Users/${userId}/PlayedItems/${movieId}`);

  // Playback: a second sign-in reports a play, the way a client does; the server then lists it under Sessions.
  const idle = await get('/Sessions');
  const player = await authenticate();
  if (player.token) {
    const ph = { [userHeader]: `MediaBrowser ${CLIENT_AUTH}, Token="${player.token}"` };
    const startPlay = await c.send({ method: 'POST', path: `${prefix}/Sessions/Playing`, headers: ph, json: { ItemId: movieId, MediaSourceId: mediaSourceId, PlaySessionId: 'deluno-recorder-play', PlayMethod: 'DirectPlay', CanSeek: true, PositionTicks: 60000000, IsPaused: false } });
    await sleep(2000);
    const playing = await get('/Sessions');
    save(kind, version, 'sessions-playing', note('A client reports the film playing at the six-second mark: the session list before and during'), [idle, startPlay, playing]);
    await c.send({ method: 'POST', path: `${prefix}/Sessions/Playing/Stopped`, headers: ph, json: { ItemId: movieId, MediaSourceId: mediaSourceId, PlaySessionId: 'deluno-recorder-play', PositionTicks: 60000000 } });
    save(kind, version, 'sessions-stopped', note('The same client stopped playing: the session list afterwards'), [await get('/Sessions')]);
  } else addMissing(`${kind} sessions-playing: a second sign-in for a playing device was refused`);
  save(kind, version, 'sessions-idle', note('No one is playing'), [idle]);

  // Refresh, update and the directory browser.
  save(kind, version, 'refresh', note('A refresh of the whole library, and a media update for the film\'s path (Modified), then one for a path the server does not hold'), [
    await send('POST', '/Library/Refresh'),
    await send('POST', '/Library/Media/Updated', { Updates: [{ Path: MOVIE_FILE, UpdateType: 'Modified' }] }),
    await send('POST', '/Library/Media/Updated', { Updates: [{ Path: '/nowhere/Not A Film (1999).mkv', UpdateType: 'Created' }] })]);
  save(kind, version, 'directory-contents', note('The folder browser Deluno uses to map paths: a folder that exists, a folder that does not'), [
    await get(`/Environment/DirectoryContents?path=${encodeURIComponent('/media/Movies')}&includeFiles=false`), await get(`/Environment/DirectoryContents?path=${encodeURIComponent('/nowhere/none')}&includeFiles=false`)]);

  // Collections, as Deluno's collection sync makes them.
  const made = await send('POST', `/Collections?Name=${encodeURIComponent('Recorded collection')}&Ids=${movieId}`);
  const collectionId = replyJson(made)?.Id;
  if (collectionId) {
    const read = await get(`/Items?Ids=${collectionId}&Fields=Overview,Tags,Genres,ProviderIds,People,Studios`);
    const item = replyJson(read).Items[0]; item.Overview = 'Recorded description';
    const describe = await send('POST', `/Items/${collectionId}`, item);
    const fav = await send('POST', `/Users/${userId}/FavoriteItems/${collectionId}`);
    const addMore = await send('POST', `/Collections/${collectionId}/Items?Ids=${seriesId}`);
    const remove = await send('DELETE', `/Collections/${collectionId}/Items?Ids=${seriesId}`);
    const unfav = await send('DELETE', `/Users/${userId}/FavoriteItems/${collectionId}`);
    save(kind, version, 'collection-sync', note('A collection made with the film, described, put on the home screen (favourite), a series added and taken out again, then listed with its members'), [
      made, read, describe, fav, addMore, remove, await get(`/Users/${userId}/Items?Recursive=true&IncludeItemTypes=BoxSet&CollapseBoxSetItems=false`), await get(`/Users/${userId}/Items?ParentId=${collectionId}&CollapseBoxSetItems=false`), await get(`/Users/${userId}/Items?Recursive=true&IncludeItemTypes=Playlist&CollapseBoxSetItems=false`)]);
    save(kind, version, 'collection-unfavourited', note('The collection taken off the home screen (DELETE /Users/{id}/FavoriteItems/{id})'), [unfav]);
    save(kind, version, 'collection-deleted', note('The collection deleted (DELETE /Items/{id}), and again'), [await send('DELETE', `/Items/${collectionId}`), await send('DELETE', `/Items/${collectionId}`)]);
  } else addMissing(`${kind} collection-sync: a collection could not be made (POST /Collections answered ${made.reply.status})`);

  // An item deleted from the library: a throwaway copy of the film, scanned in, then deleted by id.
  docker('run', '--rm', '-v', `${project}_media:/media`, '--entrypoint', 'sh', 'jellyfin/jellyfin:latest', '-c', `mkdir -p "/media/Movies/Recorded Delete (2021)" && cp "${MOVIE_FILE}" "/media/Movies/Recorded Delete (2021)/Recorded Delete (2021).mkv" && chmod -R a+rwX /media`);
  await send('POST', '/Library/Refresh');
  const thrown = await until('the throwaway film to be scanned', 120, async () => replyJson(await get('/Items?Recursive=true&IncludeItemTypes=Movie'))?.Items?.find((i) => /^Recorded Delete/.test(i.Name)), 4000).catch(() => null);
  if (thrown) {
    const before = await get('/Items?Recursive=true&IncludeItemTypes=Movie');
    const del = await send('DELETE', `/Items/${thrown.Id}`);
    save(kind, version, 'item-deleted', note('A film deleted by its id (the server removes the item and its file), then the list; and a delete of an id it does not have'), [before, del, await get('/Items?Recursive=true&IncludeItemTypes=Movie'), await send('DELETE', '/Items/00000000000000000000000000000000')]);
  } else addMissing(`${kind} item-deleted: the throwaway film was not scanned in within 2 minutes`);

  // Library/Media/Updated refused: a body the server cannot read.
  save(kind, version, 'media-updated-refused', note('POST /Library/Media/Updated with a body that is not the update list (the server refuses it), and with no body'), [
    await c.send({ method: 'POST', path: `${prefix}/Library/Media/Updated`, headers: keyHeaders, text: 'not json', contentType: 'application/json' }), await send('POST', '/Library/Media/Updated')]);

  // A user made, given a policy, and removed.
  const uname = 'rec' + randomBytes(3).toString('hex');
  const newUser = await send('POST', '/Users/New', { Name: uname });
  const newId = replyJson(newUser)?.Id;
  if (newId) {
    const pol = replyJson(await get(`/Users/${newId}`)).Policy;
    const setPol = await send('POST', `/Users/${newId}/Policy`, pol);
    save(kind, version, 'user-created', note('A user made (POST /Users/New), given its policy back, listed, then removed'), [newUser, setPol, await get('/Users'), await send('DELETE', `/Users/${newId}`)]);
  }
  save(kind, version, 'not-answering', 'A request sent to a port nothing listens on: the connection is refused.', [await new Client('http://127.0.0.1:1').get(`${prefix}/System/Info`)]);
}

// ------------------------------------------------------------------ Plex (unclaimed)

async function plex() {
  const base = `http://127.0.0.1:${PORTS.plex}`;
  const c = new Client(base, { keep: ['content-type'], headers: { accept: 'application/json', 'x-plex-client-identifier': 'deluno-recorder-1', 'x-plex-product': 'Deluno Recorder' } });
  const identity = await until('Plex to answer /identity', 300, async () => { const e = await c.get('/identity'); return e.reply.status === 200 ? e : null; }, 4000);
  const version = String(replyJson(identity).MediaContainer.version);
  console.log('plex ' + version);
  const mk0 = () => c.send({ method: 'POST', path: `/library/sections?name=Early&type=movie&agent=tv.plex.agents.movie&scanner=${encodeURIComponent('Plex Movie')}&language=en-US&location=${encodeURIComponent('/media/Movies')}` });
  const note = (what) => `${what} (Plex Media Server ${version} in Docker, UNCLAIMED: no account is made, so every call is local and carries no token).`;
  save('plex', version, 'identity', note('/identity: the unauthenticated identity reply that carries the version and whether the server is claimed'), [identity]);
  // Straight after it first answers, a fresh Plex refuses to make a library: that reply is recorded as it is, then the run waits it out.
  const early = only ? { reply: { status: 0 } } : await mk0();
  if (early.reply.status === 400) save('plex', version, 'starting-up', note('A library made while the server is still starting up (right after /identity first answered)'), [early]);
  await sleep(early.reply.status === 400 ? 90000 : 0);
  const root = await c.get('/');
  if (root.reply.status !== 200) {
    save('plex', version, 'unclaimed-root-refused', note('The server root and the library list asked with no token'), [root, await c.get('/library/sections')]);
    addMissing(`plex: an unclaimed server answered ${root.reply.status} to '/' without a token, so only /identity was recorded. Everything else (libraries, items, sessions, collections) needs the server claimed by its owner at plex.tv; no account was made`);
    return;
  }
  save('plex', version, 'root', note('The server root: name, version, machine id, claimed or not, myPlex state'), [root]);
  const mk = (n, type, agent, scanner, path) => c.send({ method: 'POST', path: `/library/sections?name=${encodeURIComponent(n)}&type=${type}&agent=${agent}&scanner=${encodeURIComponent(scanner)}&language=en-US&location=${encodeURIComponent(path)}` });
  const empty = await c.get('/library/sections');
  // Run again on a server that already holds the two libraries (REC_ONLY=<state>): nothing is created twice.
  const have = replyJson(empty)?.MediaContainer?.Directory?.length ?? 0;
  const mkM = have >= 2 ? empty : await mk('Movies', 'movie', 'tv.plex.agents.movie', 'Plex Movie', '/media/Movies');
  const mkS = have >= 2 ? empty : await mk('Shows', 'show', 'tv.plex.agents.series', 'Plex TV Series', '/media/Shows');
  save('plex', version, 'libraries-created', note('No library, then a movies section and a TV section made over the two folders'), [empty, mkM, mkS, await c.get('/library/sections')]);
  const sections = replyJson(await c.get('/library/sections'))?.MediaContainer?.Directory ?? [];
  if (sections.length < 2) { addMissing(`plex: the server would not make library sections without an account (POST /library/sections answered ${mkM.reply.status})`); return; }
  const mKey = sections.find((s) => s.type === 'movie').key;
  const sKey = sections.find((s) => s.type === 'show').key;
  await until('the sections to find the film and series', 300, async () => {
    const a = replyJson(await c.get(`/library/sections/${mKey}/all?type=1&includeGuids=1`)); const b = replyJson(await c.get(`/library/sections/${sKey}/all?type=2&includeGuids=1`));
    return a?.MediaContainer?.size >= 1 && b?.MediaContainer?.size >= 1;
  }, 5000);
  await sleep(15000);
  const movies = await c.get(`/library/sections/${mKey}/all?type=1&includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=500`);
  const shows = await c.get(`/library/sections/${sKey}/all?type=2&includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=500`);
  const movieKey = replyJson(movies).MediaContainer.Metadata[0].ratingKey;
  const showKey = replyJson(shows).MediaContainer.Metadata[0].ratingKey;
  save('plex', version, 'sections', note('The library sections as Deluno lists them'), [await c.get('/library/sections')]);
  save('plex', version, 'catalogue-movies', note('The movie section with guids (provider ids)'), [movies, await c.get(`/library/metadata/${movieKey}`)]);
  save('plex', version, 'catalogue-series', note('The TV section with guids (shows, seasons and episodes as Deluno asks for the three levels), and one show in full'), [
    shows, await c.get(`/library/sections/${sKey}/all?type=3&includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=500`), await c.get(`/library/sections/${sKey}/all?type=4&includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=500`),
    await c.get(`/library/metadata/${showKey}`), await c.get(`/library/metadata/${showKey}/allLeaves`)]);
  save('plex', version, 'accounts', note('The server\'s own account list and the myPlex account state (unclaimed: no plex.tv user)'), [await c.get('/accounts'), await c.get('/myplex/account')]);
  save('plex', version, 'sessions-idle', note('No one is playing'), [await c.get('/status/sessions')]);
  const scrobble = await c.get(`/:/scrobble?key=${movieKey}&identifier=com.plexapp.plugins.library`);
  save('plex', version, 'watched', note('The film scrobbled (marked watched), then read with its viewCount, and the movie section listed as Deluno pages it'), [
    scrobble, await c.get(`/library/metadata/${movieKey}`), await c.get(`/library/sections/${mKey}/all?type=1&includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=500`)]);
  await c.get(`/:/unscrobble?key=${movieKey}&identifier=com.plexapp.plugins.library`);
  const tl = (state) => c.get(`/:/timeline?ratingKey=${movieKey}&key=${encodeURIComponent('/library/metadata/' + movieKey)}&state=${state}&time=6000&duration=20000&X-Plex-Client-Identifier=deluno-recorder-2`);
  const playing = await tl('playing');
  await sleep(2000);
  save('plex', version, 'sessions-playing', note('A player reports the film playing at the six-second mark (/:/timeline), and the server lists it under /status/sessions'), [playing, await c.get('/status/sessions')]);
  await tl('stopped');
  save('plex', version, 'refresh', note('A refresh of one section, of one folder in it, and of a folder the server does not hold; the trash emptied'), [
    await c.get(`/library/sections/${mKey}/refresh`), await c.get(`/library/sections/${mKey}/refresh?path=${encodeURIComponent(MOVIE_DIR)}`),
    await c.get(`/library/sections/${mKey}/refresh?path=${encodeURIComponent('/nowhere')}`), await c.send({ method: 'PUT', path: `/library/sections/${mKey}/emptyTrash` })]);
  // Plex browses a folder at /services/browse/<base64 of the path>; the older `path=` query is ignored and lists the roots of the server for any path.
  const browse = (path) => c.get(`/services/browse/${Buffer.from(path).toString('base64')}?includeFiles=0`);
  save('plex', version, 'directory-contents', note('The folder browser (/services/browse/<base64 of the folder>): the media folder, the root, a folder that does not exist, and the old path query that Plex ignores'), [
    await browse('/media/Movies'), await browse('/'), await browse('/nowhere'), await c.get(`/services/browse?path=${encodeURIComponent('/nowhere')}&includeFiles=0`)]);
  // The collection lists of both libraries before any collection exists (Deluno reads them with every catalogue listing).
  save('plex', version, 'collections-empty', note('The collections of the movie library and of the TV library, asked for as Deluno pages them, before any collection exists'), [
    await c.get(`/library/sections/${mKey}/collections?includeGuids=0&X-Plex-Container-Start=0&X-Plex-Container-Size=500`), await c.get(`/library/sections/${sKey}/collections?includeGuids=0&X-Plex-Container-Start=0&X-Plex-Container-Size=500`)]);
  const machine = replyJson(identity).MediaContainer.machineIdentifier;
  const uri = encodeURIComponent(`server://${machine}/com.plexapp.plugins.library/library/metadata/${movieKey}`);
  const made = await c.send({ method: 'POST', path: `/library/collections?type=1&title=${encodeURIComponent('Recorded collection')}&smart=0&sectionId=${mKey}&uri=${uri}` });
  const collection = replyJson(made)?.MediaContainer?.Metadata?.[0]?.ratingKey;
  if (collection) {
    const desc = await c.send({ method: 'PUT', path: `/library/sections/${mKey}/all?type=18&id=${collection}&summary.value=${encodeURIComponent('Recorded description')}&summary.locked=1` });
    save('plex', version, 'collection-sync', note('A collection made with the film, described, its home-screen promotion read, listed with its children'), [
      await c.get(`/library/sections/${mKey}/collections?includeGuids=0`), made, desc, await c.get(`/library/collections/${collection}/children?excludeAllLeaves=1`),
      await c.get(`/hubs/sections/${mKey}/manage?metadataItemId=${collection}`),
      await c.send({ method: 'POST', path: `/hubs/sections/${mKey}/manage?metadataItemId=${collection}&promotedToRecommended=1&promotedToOwnHome=1&promotedToSharedHome=1` })]);
    save('plex', version, 'collection-deleted', note('The collection deleted, and the same delete again'), [await c.send({ method: 'DELETE', path: `/library/collections/${collection}` }), await c.send({ method: 'DELETE', path: `/library/collections/${collection}` })]);
  } else addMissing(`plex collection-sync: a collection could not be made (answered ${made.reply.status})`);
  save('plex', version, 'playlists', note('The video playlists (none made)'), [await c.get('/playlists?playlistType=video')]);
  // plex.tv itself, asked with no token the way a server nobody has claimed asks (a real unauthenticated GET from the internet; nothing is signed in).
  {
    const tv = new Client('https://plex.tv', { keep: ['content-type'], headers: { accept: 'application/json', 'x-plex-client-identifier': 'deluno-recorder-1', 'x-plex-product': 'Deluno Recorder' } });
    const discover = new Client('https://discover.provider.plex.tv', { keep: ['content-type'], headers: { accept: 'application/json', 'x-plex-client-identifier': 'deluno-recorder-1', 'x-plex-product': 'Deluno Recorder' } });
    const tagged = (host, e) => ({ ...e, request: { ...e.request, path: host + e.request.path } });
    save('plex', version, 'plex-tv-no-token', note('The plex.tv calls Deluno makes for the account, its resources, the people the server is shared with and the watchlist, asked with no token at all (plex.tv answers each; the path of a request here begins with the host)'), [
      tagged('plex.tv', await tv.get('/api/v2/user')), tagged('plex.tv', await tv.get('/api/v2/resources?includeHttps=1&includeRelay=1')),
      tagged('plex.tv', await tv.get(`/api/servers/${machine}/shared_servers`)), tagged('plex.tv', await tv.get('/api/users')), tagged('plex.tv', await tv.get('/api/home/users')),
      tagged('discover.provider.plex.tv', await discover.get('/library/sections/watchlist/all?X-Plex-Container-Start=0&X-Plex-Container-Size=500'))]);
  }
  save('plex', version, 'wrong-token', note('The library list with a token the server does not know'), [await c.send({ method: 'GET', path: '/library/sections', headers: { 'x-plex-token': 'not-a-token' } })]);
  save('plex', version, 'not-answering', 'A request sent to a port nothing listens on: the connection is refused.', [await new Client('http://127.0.0.1:1').get('/identity')]);
  addMissing('plex: the plex.tv replies Deluno asks for (the user list, shared servers, resources, a token that identifies an owner) need the server claimed by its owner at plex.tv; no account was made');
}

makeMedia();
for (const name of process.argv.slice(2)) {
  if (name === 'plex') await plex();
  else if (name === 'jellyfin' || name === 'emby') await embyFamily(name);
  else { console.error('unknown: ' + name); process.exit(2); }
}
console.log('done');
