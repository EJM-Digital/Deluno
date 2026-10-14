// Finds every test in the repository and the names a coverage-register proof may cite.
//
// A proof in docs/testing/coverage-register.json is { type, file, name }. The check (scripts/check-coverage-register.mjs)
// asks this index whether `file` exists and `name` is a test, a test group or a class in it. No dependencies: plain node.
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', 'TestResults', 'dist', 'playwright-report', 'test-results']);

export function walk(dir, accept, out = []) {
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue;
    const full = join(dir, entry);
    let info;
    try { info = statSync(full); } catch { continue; }
    if (info.isDirectory()) walk(full, accept, out);
    else if (accept(full)) out.push(full);
  }
  return out;
}

export const slash = (p) => p.split('\\').join('/');
export const read = (file) => readFileSync(file, 'utf8').replace(/^\uFEFF/, '');

/** Every file that can hold a proof, as repo-relative paths. */
export function testFiles(root) {
  const files = [];
  const add = (dir, accept) => walk(join(root, dir), accept, files);
  add('tests', (f) => f.endsWith('.cs') && !/[\\/]fixtures[\\/]/.test(f));
  add('apps/web/src', (f) => /\.test\.tsx?$/.test(f));
  add('apps/web/tests', (f) => /\.spec\.ts$/.test(f) || /\.test\.tsx?$/.test(f));
  add('scripts', (f) => /\.test\.mjs$/.test(f) || /[\\/]golden-path[\\/].*\.ps1$/.test(f));
  add('services', (f) => /[\\/]test[\\/].*\.js$/.test(f) || /\.test\.(js|mjs|ts)$/.test(f));
  add('sdk', (f) => /\.test\.(js|mjs|ts)$/.test(f));
  add('integrations', (f) => /(\.test\.|[\\/]tests?[\\/]).*\.(js|mjs|ts|py)$/.test(f));
  return files.map((f) => slash(relative(root, f)));
}

const TS_TITLE = /\b(?:it|test|describe)(?:\.(?:each|skip|only|fixme|fails|concurrent|serial|describe|configure))*(?:\([^)]*\))?\(\s*(['"`])((?:\\.|(?!\1)[^\\\n])+)\1/g;
const CS_METHOD = /\[(?:[A-Za-z]*Fact|[A-Za-z]*Theory)\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:public\s+|internal\s+)?(?:static\s+)?(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)/g;
const CS_CLASS = /\b(?:class|record)\s+(\w+)/g;
const RECORDING = /RecordedReplies\b|WeirContract\.Rec|\bRecorded[A-Z]\w*\s*[(.<]|fixtures[\\/]+recorded/;

/** The names inside one test file: its tests, its groups and (C#) its classes. */
export function namesOf(file, text) {
  const names = new Set();
  if (file.endsWith('.cs')) {
    for (const m of text.matchAll(CS_METHOD)) names.add(m[1]);
    for (const m of text.matchAll(CS_CLASS)) names.add(m[1]);
  } else if (file.endsWith('.ps1')) {
    for (const m of text.matchAll(/^\s*function\s+([\w-]+)/gim)) names.add(m[1]);
    for (const m of text.matchAll(/(['"])([a-z][a-z0-9-]*(?:\.[a-z0-9*-]+)+)\1/g)) names.add(m[2]);
  } else {
    for (const m of text.matchAll(TS_TITLE)) names.add(m[2]);
  }
  return names;
}

/** Lower-case letters and digits only, so a title cited with other quotes, spaces or punctuation still finds itself. */
export const squash = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, '');

export class TestIndex {
  constructor(root) {
    this.root = root;
    this.files = new Map(); // rel -> { text, names:Set, squashed:Set, hay:string }
    for (const rel of testFiles(root)) {
      const text = read(join(root, rel));
      const names = namesOf(rel, text);
      this.files.set(rel, { text, names, squashed: new Set([...names].map(squash)), hay: squash(text), ...csBodies(rel, text) });
    }
    this.byBase = new Map();
    for (const rel of this.files.keys()) {
      const base = rel.split('/').pop();
      if (!this.byBase.has(base)) this.byBase.set(base, []);
      this.byBase.get(base).push(rel);
    }
    this.byName = new Map(); // exact name -> [files]  (C# methods, classes, scenario ids)
    for (const [rel, f] of this.files) for (const n of f.names) {
      if (!this.byName.has(n)) this.byName.set(n, []);
      this.byName.get(n).push(rel);
    }
  }

  has(file) { return this.files.has(file); }

  /** A cited name exists when it is a test, group or class of the file, or (for a long enough phrase) its squashed text is in the file. */
  hasName(file, name) {
    const f = this.files.get(file);
    if (!f) return false;
    if (f.names.has(name)) return true;
    const q = squash(name);
    if (q.length === 0) return false;
    if (f.squashed.has(q)) return true;
    // A test title cited as the start of a longer title, or the other way round, is the same test.
    if (q.length >= 14) for (const s of f.squashed) if (s.startsWith(q) || (s.length >= 14 && q.startsWith(s))) return true;
    return q.length >= 20 && f.hay.includes(q);
  }

  /**
   * Does the test replay a recorded real reply of another app? (RecordedReplies, the Weir contract helper, or a fixture path.)
   * A recording loaded in the class's setup (before its first test) counts for every test in it; otherwise the test's own body must load one.
   * With no name (or "*"), any test of the file will do.
   */
  replaysRecording(file, name) {
    const f = this.files.get(file);
    if (!f || !file.endsWith('.cs')) return false;
    if (RECORDING.test(f.header ?? '')) return true;
    if (!name || name === '*') return RECORDING.test(f.text);
    const body = f.bodies?.get(name);
    if (body !== undefined) return RECORDING.test(body);
    // A class name or a cited fragment: any test whose name starts with it.
    const q = squash(name);
    for (const [n, text] of f.bodies ?? []) if (squash(n).startsWith(q) && RECORDING.test(text)) return true;
    return false;
  }
}

/** Splits a C# test file into the text before its first test and the body of each test. */
function csBodies(file, text) {
  if (!file.endsWith('.cs')) return {};
  const starts = [...text.matchAll(CS_METHOD)].map((m) => ({ name: m[1], at: m.index }));
  if (starts.length === 0) return { header: text, bodies: new Map() };
  const bodies = new Map();
  for (let i = 0; i < starts.length; i++) bodies.set(starts[i].name, text.slice(starts[i].at, i + 1 < starts.length ? starts[i + 1].at : text.length));
  return { header: text.slice(0, starts[0].at), bodies };
}

/** The kind of proof a file is. */
export function proofTypeOf(file, index, name) {
  if (/\.ps1$/.test(file)) return 'scenario';
  if (/^apps\/web\/tests\/.+\.spec\.ts$/.test(file)) return 'smoke';
  if (/\.test\.tsx?$/.test(file)) return 'vitest';
  if (index && index.replaysRecording(file, name)) return 'recording-replay';
  return 'backend';
}
