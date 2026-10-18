// Shared by the Docker recorders (#1237): records the real replies of an app running in a container, the same way
// scripts/recorded-replies/Record.Lib.ps1 does in the golden VM, and writes them through scrub.mjs.
//
// Nothing here knows a host or a login: the recorder passes the base address of an app on this machine (the container's published port)
// and any login it used goes in `secrets` so it is removed from the recording by exact value as a second line behind scrub.mjs.
import { mkdirSync, writeFileSync, existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { scrubRecording, tidyRecording, findLeaks, scrubbedValue } from '../scrub.mjs';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
export const recordedRoot = join(repository, 'tests', 'fixtures', 'recorded');

const secrets = new Set();
export function addSecret(value) { if (value && String(value).length >= 4) secrets.add(String(value)); }

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export async function until(what, seconds, read, everyMs = 1000) {
  const end = Date.now() + seconds * 1000;
  let last;
  while (Date.now() < end) {
    try { const v = await read(); if (v) return v; } catch (e) { last = e; }
    await sleep(everyMs);
  }
  throw new Error(`Gave up after ${seconds} s waiting for ${what}.${last ? ' Last error: ' + String(last).slice(0, 300) : ''}`);
}

/** One app as seen from outside its container: a cookie jar so a login sticks, and the headers worth keeping on a reply. */
export class Client {
  constructor(base, { keep = ['content-type'], headers = {} } = {}) {
    this.base = base.replace(/\/+$/, '');
    this.keep = keep;
    this.headers = headers;
    this.cookies = new Map();
  }

  /** Sends one request and returns the exchange exactly as the app answered it (any status is a result). */
  async send({ method = 'GET', path, json, form, text, multipart, headers = {}, contentType, bodyNote, keep }) {
    const init = { method, headers: { ...this.headers, ...headers }, redirect: 'manual' };
    let recordedBody;
    if (form) { const p = new URLSearchParams(form); init.body = p.toString(); init.headers['content-type'] = 'application/x-www-form-urlencoded'; recordedBody = p.toString(); }
    else if (multipart) {
      const f = new FormData(); const shown = [];
      for (const [k, v] of Object.entries(multipart)) {
        if (v instanceof Uint8Array) { f.append(k, new Blob([v], { type: 'application/x-bittorrent' }), 'file.torrent'); shown.push(`${k}=<${v.length} bytes>`); }
        else if (v && v.bytes) { f.append(k, new Blob([v.bytes], { type: v.type }), v.filename); shown.push(`${k}=<${v.bytes.length} bytes, ${v.filename}>`); }
        else { f.append(k, String(v)); shown.push(`${k}=${v}`); }
      }
      init.body = f; recordedBody = bodyNote ?? shown.join('&');
    } else if (json !== undefined) { recordedBody = JSON.stringify(json); init.body = recordedBody; init.headers['content-type'] = 'application/json'; }
    else if (text !== undefined) { recordedBody = text; init.body = text; init.headers['content-type'] = contentType ?? 'text/plain'; }
    if (this.cookies.size) init.headers.cookie = [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ');
    const request = { method: method.toUpperCase(), path };
    if (recordedBody !== undefined) request.bodyText = recordedBody;
    let reply;
    try {
      const response = await fetch(this.base + path, init);
      for (const c of response.headers.getSetCookie?.() ?? []) { const [pair] = c.split(';'); const i = pair.indexOf('='); this.cookies.set(pair.slice(0, i), pair.slice(i + 1)); }
      const bodyText = await response.text();
      const headersKept = {};
      for (const name of keep ?? this.keep) { const v = response.headers.get(name); if (v !== null) headersKept[name] = v; }
      reply = { status: response.status, headers: headersKept, bodyText };
    } catch (error) {
      reply = { status: 0, headers: {}, error: String(error.cause?.message ?? error.message) };
    }
    return { request, reply };
  }
  get(path, opts = {}) { return this.send({ ...opts, path }); }
}

/** The JSON of an exchange's reply, or null. */
export function replyJson(exchange) {
  try { return exchange.reply.bodyText ? JSON.parse(exchange.reply.bodyText) : null; } catch { return null; }
}

export const missing = [];
export function addMissing(text) { missing.push(text); console.log('MISSING ' + text); }

/** Saves one state under tests/fixtures/recorded/<app>/<version>/<state>.json, scrubbed. */
export function save(app, version, state, note, exchanges) {
  if (!exchanges || exchanges.length === 0) { addMissing(`${app} ${version} ${state} (nothing was recorded)`); return; }
  const safeVersion = String(version).replace(/[^0-9A-Za-z.\-+]/g, '_');
  let recording = {
    app, version: safeVersion, state, recordedAt: new Date().toISOString().replace(/\.\d+Z$/, 'Z'), note, exchanges: exchanges.map((e) => JSON.parse(JSON.stringify(e)))
  };
  let text = JSON.stringify(recording);
  for (const s of secrets) text = text.split(s).join(scrubbedValue);
  recording = tidyRecording(scrubRecording(JSON.parse(text)));
  const out = JSON.stringify(recording, null, 2) + '\n';
  const leaks = findLeaks(out);
  if (leaks.length) throw new Error(`${app}/${state} still carries: ${leaks.join('; ')}`);
  const file = join(recordedRoot, app, safeVersion, `${state}.json`);
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, out);
  console.log(`recorded ${app} ${safeVersion} ${state} (${exchanges.length} exchange(s))`);
}

export function readRecording(app, version, state) {
  const file = join(recordedRoot, app, version, `${state}.json`);
  return existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : null;
}

export const ports = {
  deluno: Number(process.env.REC_DELUNO_PORT ?? 35099), qbittorrent: Number(process.env.REC_QBIT_PORT ?? 38081),
  transmission: Number(process.env.REC_TRANSMISSION_PORT ?? 39091), deluge: Number(process.env.REC_DELUGE_PORT ?? 38112),
  sabnzbd: Number(process.env.REC_SABNZBD_PORT ?? 38083), nzbget: Number(process.env.REC_NZBGET_PORT ?? 36789),
  weir: Number(process.env.REC_WEIR_PORT ?? 39347), fixture: Number(process.env.REC_FIXTURE_PORT ?? 38765),
  sonarr: Number(process.env.REC_SONARR_PORT ?? 38989), radarr: Number(process.env.REC_RADARR_PORT ?? 37878), prowlarr: Number(process.env.REC_PROWLARR_PORT ?? 39696)
};
export const local = (name) => `http://127.0.0.1:${ports[name]}`;
/** The fixture makes a release (a torrent with a web seed, or a Usenet release) and offers it. */
export async function makeFilm(body) {
  // A release name is made once per fixture run; a second recording run asks again with a number after the title.
  for (let n = 0; n < 30; n++) {
    const title = n === 0 ? body.title : `${body.title} ${n + 1}`;
    const r = await fetch(`${local('fixture')}/films`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ year: 2008, extra: false, usenet: false, layout: 'file', ...body, title }) });
    if (r.ok) return await r.json();
    const text = await r.text();
    if (!/already exists/.test(text)) throw new Error(`fixture would not make ${title}: ${r.status} ${text}`);
  }
  throw new Error(`fixture has too many releases named ${body.title}`);
}
export async function torrentBytes(film) {
  const name = film.url.split('/dl/')[1];
  const r = await fetch(`${local('fixture')}/dl/${name}`);
  if (!r.ok) throw new Error(`torrent ${name}: ${r.status}`);
  return new Uint8Array(await r.arrayBuffer());
}
