// Fails when a recording of Deluno's own side of its talk with Weir changes without its manifest being updated (#1239).
//
// tests/fixtures/recorded/deluno/ holds what the real Deluno sends Weir and what Weir sends it (recorded by a proxy between the two),
// and Deluno's own replies to the calls Weir makes (recorded from the server code). Weir's tests copy that folder and replay it, so a
// recording must never change silently on either side. tests/fixtures/recorded/deluno/MANIFEST.sha256 lists a SHA-256 for every file:
//
//   <64 hex characters>  <version>/<state>.json
//
// A file's hash is taken over its text with line endings read as LF, so a Windows checkout (CRLF) and a Linux one agree. The lines are
// sorted by name, and the file ends with a newline. The Weir session copies the folder with its manifest, runs this same check
// there (`node check-recorded-manifest.mjs --root <folder> --manifest <manifest>`), and builds its replay tests on the recordings.
//
// Usage: node scripts/check-recorded-manifest.mjs [--root <folder>] [--manifest <file>] [--write]
//   --write   rewrites the manifest to match the folder. Do this only on purpose, with the recordings' change explained in the commit.
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = fileURLToPath(new URL('..', import.meta.url));
export const defaultRoot = join(repositoryRoot, 'tests', 'fixtures', 'recorded', 'deluno');
export const manifestName = 'MANIFEST.sha256';

export function hashOf(file) {
  const text = readFileSync(file, 'utf8').replace(/^﻿/, '').replace(/\r\n/g, '\n');
  return createHash('sha256').update(text, 'utf8').digest('hex');
}

function* recordings(root) {
  for (const version of readdirSync(root).sort()) {
    const folder = join(root, version);
    if (!statSync(folder).isDirectory()) continue;
    for (const file of readdirSync(folder).sort()) if (file.endsWith('.json')) yield { name: `${version}/${file}`, path: join(folder, file) };
  }
}

export function currentManifest(root) {
  const lines = [...recordings(root)].map(({ name, path }) => ({ name, line: `${hashOf(path)}  ${name}` }));
  lines.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  return lines.map((entry) => entry.line).join('\n') + '\n';
}

export function problems(root, manifestPath) {
  if (!existsSync(manifestPath)) return [`${manifestPath} is missing. Write it with: node scripts/check-recorded-manifest.mjs --write`];
  const read = (text) => new Map(text.split(/\r?\n/).filter((line) => line.trim()).map((line) => {
    const match = /^([0-9a-f]{64}) {2}(\S.*)$/.exec(line);
    return match ? [match[2], match[1]] : [line, null];
  }));
  const listed = read(readFileSync(manifestPath, 'utf8'));
  const found = read(currentManifest(root));
  const out = [];
  for (const [name, hash] of found) {
    if (!listed.has(name)) out.push(`${name} is not in the manifest (a new recording).`);
    else if (listed.get(name) !== hash) out.push(`${name} changed: the manifest holds ${listed.get(name)?.slice(0, 12)}, the file is ${hash.slice(0, 12)}.`);
  }
  for (const [name, hash] of listed) {
    if (hash === null) out.push(`The manifest has a line that is not "<sha256>  <version>/<state>.json": ${name}`);
    else if (!found.has(name)) out.push(`${name} is in the manifest but the file is gone.`);
  }
  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const args = process.argv.slice(2);
  const option = (name, fallback) => (args.includes(name) ? resolve(args[args.indexOf(name) + 1]) : fallback);
  const root = option('--root', defaultRoot);
  const manifest = option('--manifest', join(root, manifestName));
  if (args.includes('--write')) {
    writeFileSync(manifest, currentManifest(root));
    console.log(`Wrote ${relative(process.cwd(), manifest)}.`);
  } else {
    const found = problems(root, manifest);
    if (found.length > 0) {
      console.error("Deluno's recordings and their manifest disagree (#1239):");
      for (const problem of found) console.error('  ' + problem);
      console.error('If the change is on purpose, say why in the commit and run: node scripts/check-recorded-manifest.mjs --write. Weir copies this folder and its manifest, so tell the Weir session.');
      process.exit(1);
    }
    console.log(`Every recording under ${relative(process.cwd(), root) || '.'} matches its manifest.`);
  }
}
