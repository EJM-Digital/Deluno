// Fails when a test in tests/** builds an external app's reply inline instead of loading a recording (#1237).
//
// Why: tests built on replies written from memory kept passing while the real apps answered differently
// (SABnzbd 5.1.3's `storage` is a file, not a folder; NZBGet's sizes are 32-bit halves; Weir's 409
// `handoff_ended`). A test of a download client, indexer, media server or processor takes its replies from
// tests/fixtures/recorded/<app>/<version>/<state>.json (tests/fixtures/recorded/README.md).
//
// How: every C# string literal in tests/**/*.cs is looked at; one that carries a JSON key only a certain app
// answers with (the signatures below) is an inline reply. A file that has one must be listed in
// tests/recorded-reply-allow-list.json:
//   kind "deliberate": the test needs a reply no real app sends (truncated, wrong type). A reason is required.
//   kind "pending":    not yet moved onto a recording (#1237 step 4). Each is removed when its file is converted,
//                      and the check fails for a listed file that no longer has an inline reply.
//
// Usage: node scripts/check-recorded-replies.mjs [--root <repo>] [--list]   (--list prints the files that need entries)
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Keys only these apps answer with. `all` keys must all be present in the one literal.
export const signatures = [
  { app: 'SABnzbd', any: ['nzo_id', 'noofslots', 'nzo_ids'] },
  { app: 'NZBGet', any: ['NZBID', 'NZBName', 'FileSizeHi', 'FileSizeLo', 'RemainingSizeHi', 'DownloadedSizeLo'] },
  { app: 'qBittorrent', any: ['content_path', 'dlspeed', 'added_on', 'tracker_status'] },
  { app: 'qBittorrent', all: ['hash', 'state'] },
  { app: 'Transmission', any: ['percentDone', 'hashString', 'rateDownload', 'peersConnected', 'downloadedEver'] },
  { app: 'Deluge', any: ['total_done', 'move_completed_path', 'num_seeds', 'download_payload_rate', 'is_finished'] },
  { app: 'Weir', any: ['watched_folder', 'manager_library_key', 'folders_from_manager', 'queuePosition', 'watchedPath', 'handoff_ended', 'handoff_not_finished', 'outcome_already_recorded', 'handoff-status', 'handoff-outcome', 'library-folders'] }
];

const keyPattern = (key) => new RegExp(`["\\\\]{1,2}${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}["\\\\]{1,2}\\s*:|\\b${key.replace(/[-]/g, '\\-')}\\b`);

// Every string literal in a C# source: raw ("""), verbatim (@"), interpolated ($"), and regular. Comments and char literals are skipped.
export function* literals(source) {
  let i = 0;
  let line = 1;
  const n = source.length;
  while (i < n) {
    const c = source[i];
    if (c === '\n') { line++; i++; continue; }
    if (c === '/' && source[i + 1] === '/') { while (i < n && source[i] !== '\n') i++; continue; }
    if (c === '/' && source[i + 1] === '*') {
      i += 2;
      while (i < n && !(source[i] === '*' && source[i + 1] === '/')) { if (source[i] === '\n') line++; i++; }
      i += 2;
      continue;
    }
    if (c === "'") {
      i++;
      while (i < n && source[i] !== "'") { if (source[i] === '\\') i++; i++; }
      i++;
      continue;
    }
    // A string starts at an optional run of $ and @, then ".
    if (c === '"' || ((c === '$' || c === '@') && /^[$@]{1,3}"/.test(source.slice(i, i + 4)))) {
      const start = line;
      let j = i;
      let verbatim = false;
      while (source[j] === '$' || source[j] === '@') { if (source[j] === '@') verbatim = true; j++; }
      let quotes = 0;
      while (source[j] === '"') { quotes++; j++; }
      let text = '';
      if (quotes >= 3) {
        const close = '"'.repeat(quotes);
        const end = source.indexOf(close, j);
        const stop = end === -1 ? n : end;
        text = source.slice(j, stop);
        j = stop + quotes;
      } else if (quotes === 2) {
        // An empty string "".
        j = j; // nothing inside
      } else {
        while (j < n) {
          if (verbatim) {
            if (source[j] === '"' && source[j + 1] === '"') { text += '""'; j += 2; continue; }
            if (source[j] === '"') break;
          } else {
            if (source[j] === '\\') { text += source.slice(j, j + 2); j += 2; continue; }
            if (source[j] === '"') break;
            if (source[j] === '\n') break;
          }
          text += source[j++];
        }
        j++;
      }
      yield { text, line: start };
      for (let k = i; k < j && k < n; k++) if (source[k] === '\n') line++;
      i = j;
      continue;
    }
    i++;
  }
}

export function innerReplies(source) {
  const found = [];
  for (const literal of literals(source)) {
    if (literal.text.length < 6) continue;
    for (const signature of signatures) {
      const keys = signature.any ?? signature.all;
      const hits = keys.filter((key) => keyPattern(key).test(literal.text));
      if (signature.any ? hits.length > 0 : hits.length === keys.length) {
        found.push({ app: signature.app, line: literal.line, key: hits[0] });
        break;
      }
    }
  }
  return found;
}

function* testFiles(directory) {
  for (const entry of readdirSync(directory)) {
    if (['bin', 'obj', 'node_modules', 'fixtures'].includes(entry)) continue;
    const full = join(directory, entry);
    if (statSync(full).isDirectory()) yield* testFiles(full);
    else if (entry.endsWith('.cs')) yield full;
  }
}

export function check(root) {
  const problems = [];
  const allowPath = join(root, 'tests', 'recorded-reply-allow-list.json');
  const listed = new Map();
  if (existsSync(allowPath)) {
    const allow = JSON.parse(readFileSync(allowPath, 'utf8'));
    for (const entry of allow.entries ?? []) {
      if (!entry.file || !['deliberate', 'pending'].includes(entry.kind)) problems.push(`${allowPath}: an entry needs a file and a kind of "deliberate" or "pending".`);
      else if (entry.kind === 'deliberate' && (entry.reason ?? '').trim().length < 20) problems.push(`tests/recorded-reply-allow-list.json: ${entry.file} is deliberate but gives no reason (say why no real app's reply would do).`);
      else listed.set(entry.file, entry);
    }
  }

  const offenders = new Map();
  const testsRoot = join(root, 'tests');
  if (existsSync(testsRoot)) {
    for (const file of testFiles(testsRoot)) {
      const rel = relative(root, file).split('\\').join('/');
      const replies = innerReplies(readFileSync(file, 'utf8'));
      if (replies.length > 0) offenders.set(rel, replies);
    }
  }

  for (const [file, replies] of offenders) {
    if (listed.has(file)) continue;
    const first = replies[0];
    problems.push(`${file}:${first.line}: builds ${first.app}'s reply inline (${first.key}${replies.length > 1 ? `, and ${replies.length - 1} more` : ''}). Load it from tests/fixtures/recorded/<app>/<version>/<state>.json instead (RecordedReplies.Load). If the test needs a malformed reply no real app sends, add the file to tests/recorded-reply-allow-list.json as "deliberate" with the reason.`);
  }
  for (const [file, entry] of listed) {
    if (!existsSync(join(root, file))) problems.push(`tests/recorded-reply-allow-list.json: ${file} no longer exists; remove its entry.`);
    else if (!offenders.has(file)) problems.push(`tests/recorded-reply-allow-list.json: ${file} no longer builds a reply inline; remove its ${entry.kind} entry.`);
  }
  return { problems, offenders };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const args = process.argv.slice(2);
  const rootIndex = args.indexOf('--root');
  const root = resolve(rootIndex >= 0 ? args[rootIndex + 1] : fileURLToPath(new URL('..', import.meta.url)));
  const { problems, offenders } = check(root);
  if (args.includes('--list')) {
    for (const [file, replies] of offenders) console.log(`${file}  (${[...new Set(replies.map((r) => r.app))].join(', ')})`);
    process.exit(0);
  }
  if (problems.length > 0) {
    console.error('Tests that build an external app\'s reply inline (#1237):');
    for (const problem of problems) console.error('  ' + problem);
    process.exit(1);
  }
  console.log(`No test builds an external app's reply inline (${offenders.size} listed in tests/recorded-reply-allow-list.json).`);
}
