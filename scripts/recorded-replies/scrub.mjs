// Removes everything private from a recorded reply before it is committed (#1237).
//
// The patterns for the maintainer's own network are the ones in
// scripts/check-public-exposure.ps1 (scrub.test.mjs fails if the two lists drift). On top of
// those it removes what a real app's reply carries that an outsider must never see: API keys,
// passwords, session cookies and tokens, any private LAN address, UNC share names, and the
// signed-in user's profile folder.
//
// Usage:  node scripts/recorded-replies/scrub.mjs <file-or-folder> [--name <machine>]... [--check]
//   --name   a machine, user or host name to remove too (the capture script passes the VM's).
//   --check  change nothing; exit 1 and list what a recording still carries.
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

// Same literals, built the same way, as check-public-exposure.ps1 (never written out whole in a tracked file).
export const exposurePatterns = [
  { name: 'a LAN address on the home network', regex: new RegExp('10' + '\\.1\\.1\\.\\d{1,3}', 'g'), replacement: '192.168.1.10' },
  { name: 'the NAS name', regex: new RegExp('storage' + '-city', 'gi'), replacement: '<nas>' },
  { name: "the rig's machine name", regex: new RegExp('WIN-MJCOV' + 'DR5C88', 'gi'), replacement: '<server>' },
  { name: "the workstation's name", regex: new RegExp('SPARE' + '-PC', 'gi'), replacement: '<workstation>' },
  { name: 'a lab password', regex: new RegExp('Deluno-Lab-' + '2026', 'g'), replacement: '<password>' },
  {
    name: 'an e-mail address',
    regex: /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/g,
    replacement: 'user@example.test',
    keep: (match) => /noreply/i.test(match) || /@example\.(test|com)$/i.test(match)
  }
];

// Beyond the maintainer's own network: any private address a real reply can carry.
export const extraPatterns = [
  // 10.x, 172.16-31.x, 192.168.x (192.168.1.10 is the placeholder and stays).
  { name: 'a private LAN address', regex: /\b(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3})\b/g, replacement: '192.168.1.10', keep: (m) => m === '192.168.1.10' },
  // \\machine\share, as text (one backslash each) or as JSON text (two each).
  // A lone n, r, t, b or f after the slashes is a JSON-escaped newline, tab or the like (a Python traceback inside a reply's text), not a machine.
  { name: 'a share on a network machine', regex: /(?<=^|[\s"'=,(\[])(\\{2,4})(?![?<])(?![nrtbf]\\)[A-Za-z0-9._-]+(?=\\)/g, replacement: '$1<nas>' },
  { name: "a user's profile folder", regex: /([A-Za-z]:\\{1,2}Users\\{1,2})(?!<|Public\b|Default\b|All Users\b)[^\\/"'\s]+/gi, replacement: '$1<user>' },
  { name: 'a home folder of a person', regex: /(\/home\/)(?!<)[A-Za-z0-9._-]+/g, replacement: '$1<user>' }
];

const secretKeys = /^(api[-_]?key|apikey|password|passwd|pass|token|access[-_]?token|refresh[-_]?token|secret|webhook[-_]?secret|sid|session[-_]?id|cookie|set-cookie|authorization|proxy-authorization|x-api-key|x-webhook-secret|x-transmission-session-id|username|user|_session_id|auth|key|nzbkey|nzb_key|rpc[-_]?password|rpc[-_]?username|controlpassword|controlusername|restrictedpassword|restrictedusername|webui_password|webui_username)$/i;
// Any key that names a login, a key or a secret, wherever it sits and however it is spelled (web_ui_username, ControlPassword, api_key...).
const sensitiveKeyPart = /(password|passwd|passphrase|secret|api[-_]?key|apikey|token|cookie|username|user_name|passkey|session[-_]?id|credential|machine[-_]?name|computer[-_]?name|host[-_]?name)/i;
export const scrubbedValue = '<scrubbed>';

function patternsFor(extraNames) {
  const names = extraNames.filter(Boolean).map((n) => ({
    name: `the name ${n}`,
    regex: new RegExp(n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'gi'),
    replacement: '<host>'
  }));
  return [...exposurePatterns, ...extraPatterns, ...names];
}

export function scrubText(text, extraNames = []) {
  let out = text;
  for (const p of patternsFor(extraNames)) {
    out = out.replace(p.regex, (...args) => {
      const match = args[0];
      if (p.keep && p.keep(match)) return match;
      const groups = args.slice(1).filter((g) => typeof g === 'string');
      return p.replacement.replace(/\$(\d)/g, (_, n) => groups[Number(n) - 1] ?? '');
    });
  }
  // key=value pairs in a query string or a form body.
  out = out.replace(/(^|[?&;,\s])(apikey|api_key|password|token|x-plex-token|x-emby-token|sid|key|username|user|nzbkey|ma_username|ma_password|rpc-password)=([^&\s"']+)/gi, (_, lead, key) => `${lead}${key}=${scrubbedValue}`);
  // Cookie and token text that is not under a key we recognise.
  out = out.replace(/\b(SID|_session_id|session_id)=([^;\s"']+)/g, (_, key) => `${key}=${scrubbedValue}`);
  out = out.replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{8,}/g, `Bearer ${scrubbedValue}`);
  return out;
}

export function scrubValue(value, extraNames = [], key = null) {
  if (typeof value === 'string') {
    if (key !== null && (secretKeys.test(key) || sensitiveKeyPart.test(key)) && value !== '') return scrubbedValue;
    return scrubText(value, extraNames);
  }
  if (Array.isArray(value)) return value.map((v) => scrubValue(v, extraNames, key));
  if (value && typeof value === 'object') {
    const out = {};
    // A setting listed as { Name: 'ControlPassword', Value: '...' } (NZBGet's config): the name says what the value is.
    const named = typeof value.Name === 'string' && typeof value.Value === 'string' && sensitiveKeyPart.test(value.Name);
    for (const [k, v] of Object.entries(value)) out[scrubText(k, extraNames)] = named && k === 'Value' && v !== '' ? scrubbedValue : scrubValue(v, extraNames, k);
    return out;
  }
  return value;
}

// A recording is { app, version, state, exchanges: [{ request: {method,path,body}, reply: {status,headers,body|bodyText} }] }.
// A body that is JSON text inside a string (bodyText) is scrubbed as parsed JSON when it parses, so key names are seen.
export function scrubRecording(recording, extraNames = []) {
  const copy = JSON.parse(JSON.stringify(recording));
  for (const exchange of copy.exchanges ?? []) {
    for (const part of [exchange.request, exchange.reply]) {
      if (!part) continue;
      if (typeof part.bodyText === 'string') {
        try {
          part.bodyText = JSON.stringify(scrubValue(JSON.parse(part.bodyText), extraNames));
        } catch {
          part.bodyText = scrubText(part.bodyText, extraNames);
        }
      }
    }
  }
  return scrubValue(copy, extraNames);
}

// JSON that arrived as text becomes JSON in the file, so a recording reads. Left as text when a number would not survive
// the round trip (an integer past 2^53) or when it is not JSON.
export function tidyRecording(recording) {
  for (const exchange of recording.exchanges ?? []) {
    for (const part of [exchange.request, exchange.reply]) {
      if (!part || typeof part.bodyText !== 'string' || part.body !== undefined) continue;
      const text = part.bodyText.trim();
      if (!/^[[{]/.test(text) || /[0-9]{16,}/.test(text)) continue;
      try { part.body = JSON.parse(text); delete part.bodyText; } catch { /* stays text */ }
    }
  }
  return recording;
}

export function findLeaks(text, extraNames = []) {
  const leaks = [];
  for (const p of patternsFor(extraNames)) {
    for (const m of text.matchAll(p.regex)) {
      if (p.keep && p.keep(m[0])) continue;
      // Already replaced by this scrubber: the placeholder folders and shares are not leaks.
      if (/<(user|nas|host)>/.test(text.slice(m.index, m.index + m[0].length + 8)) && /^(\\{2,4}|[A-Za-z]:\\{1,2}Users\\{1,2}|\/home\/)/.test(m[0]) && /<(user|nas|host)>/.test(m[0] + text.slice(m.index + m[0].length, m.index + m[0].length + 8))) continue;
      leaks.push(`${p.name}: ${m[0]}`);
    }
  }
  return leaks;
}

function* jsonFiles(path) {
  if (statSync(path).isDirectory()) {
    for (const entry of readdirSync(path)) yield* jsonFiles(join(path, entry));
  } else if (['.json', '.log'].includes(extname(path))) yield path;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const args = process.argv.slice(2);
  const names = [];
  let check = false;
  const targets = [];
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--name') names.push(args[++i]);
    else if (args[i] === '--check') check = true;
    else targets.push(args[i]);
  }
  if (targets.length === 0) {
    console.error('Usage: node scrub.mjs <file-or-folder> [--name <machine>]... [--check]');
    process.exit(2);
  }
  let bad = 0;
  for (const target of targets) {
    for (const file of jsonFiles(target)) {
      const text = readFileSync(file, 'utf8').replace(/^﻿/, '');
      if (check) {
        const leaks = findLeaks(text, names);
        if (leaks.length) { bad++; console.error(`${file}\n  ${leaks.join('\n  ')}`); }
        continue;
      }
      const scrubbed = extname(file) === '.log'
        ? scrubText(text, names)
        : JSON.stringify(tidyRecording(scrubRecording(JSON.parse(text), names)), null, 2) + '\n';
      if (scrubbed !== text) writeFileSync(file, scrubbed);
    }
  }
  process.exit(bad ? 1 : 0);
}
