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

/** A packaging script that starts the shipped container (and, as a proof file, the workflows whose steps do). */
const CONTAINER_SCRIPT = /(?:^|\/)scripts\/(?:[^/]*\/)*[^/]*(?:docker|compose|container)[^/]*\.(?:mjs|ps1|sh)$/;
export const CONTAINER_SMOKE_FILE = /^(?:\.github\/workflows\/[^/]+\.ya?ml|scripts\/(?:[^/]*\/)*[^/]*(?:docker|compose|container)[^/]*\.(?:mjs|ps1|sh))$/;

/** Every file that can hold a proof, as repo-relative paths. */
export function testFiles(root) {
  const files = [];
  const add = (dir, accept) => walk(join(root, dir), accept, files);
  add('tests', (f) => f.endsWith('.cs') && !/[\\/]fixtures[\\/]/.test(f));
  add('apps/web/src', (f) => /\.test\.tsx?$/.test(f));
  add('apps/web/tests', (f) => /\.spec\.ts$/.test(f) || /\.test\.tsx?$/.test(f));
  // The CI end-to-end suite: Playwright specs (and the helpers that define their tests) against Docker Compose, and later against a Windows runner.
  add('tests/e2e', (f) => f.endsWith('.ts') && !/playwright\.config\.ts$/.test(f));
  add('tests/e2e-windows', (f) => f.endsWith('.ts') && !/playwright\.config\.ts$/.test(f));
  add('scripts', (f) => /\.test\.mjs$/.test(f) || /[\\/]golden-path[\\/].*\.ps1$/.test(f) || CONTAINER_SCRIPT.test(slash(f)));
  add('.github/workflows', (f) => /\.ya?ml$/.test(f));
  add('services', (f) => /[\\/]test[\\/].*\.js$/.test(f) || /\.test\.(js|mjs|ts)$/.test(f));
  add('sdk', (f) => /\.test\.(js|mjs|ts)$/.test(f));
  add('integrations', (f) => /(\.test\.|[\\/]tests?[\\/]).*\.(js|mjs|ts|py)$/.test(f));
  return files.map((f) => slash(relative(root, f)));
}

const TS_TITLE = /\b(?:it|test|describe)(?:\.(?:each|skip|only|fixme|fails|concurrent|serial|describe|configure))*(?:\([^)]*\))?\(\s*(['"`])((?:\\.|(?!\1)[^\\\n])+)\1/g;
const CS_METHOD = /\[(?:[A-Za-z]*Fact|[A-Za-z]*Theory)\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:public\s+|internal\s+)?(?:static\s+)?(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)/g;
const CS_CLASS = /\b(?:class|record)\s+(\w+)/g;
// real-fs: the test (or its class setup) creates a real temp folder or a real SQLite file, works on it with real file or database code,
// and neither names a fake or in-memory file system or store. What cannot be decided is refused.
const FS_CREATE = /Path\.GetTempPath\s*\(|CreateTempSubdirectory\s*\(|Path\.GetTempFileName\s*\(|\bTestStorage\.Create\s*\(|\bMigratedDatabaseTemplate\b|\bnew\s+ApplicationTestHost\b|\bApplicationTestHost\.(?:Create|Start)\w*\s*\(|\bnew\s+(?:TempFolder|TemporaryFolder|TempRoot|TempDataRoot|ScratchFolder|Scratch)\s*\(/;
const FS_WORK = /\b(?:File|Directory|FileInfo|DirectoryInfo)\.|\bnew\s+(?:FileInfo|DirectoryInfo)\b|CreateHardLink|SqliteConnection|SqliteDatabaseConnectionFactory|\bMigrat\w*/;
const FS_FAKE = /ScriptedStateFileSystem|\bIFileSystem\b|MockFileSystem|MemoryFileSystem|\bFake\w*(?:FileSystem|Storage|Repository|Database)\b|\bInMemory\w*(?:FileSystem|Storage|Store|Repository|Database)\b|\bNull\w*Repository\b|:memory:|Mode=Memory|new\s+Mock<\s*I\w*(?:File|Storage|Disk|Database|Repository)|Substitute\.For<\s*I\w*(?:File|Storage|Disk|Database|Repository)/;
// container-smoke: a packaging step or script that starts the shipped container image (docker run, or docker compose up on the shipped file).
const CONTAINER_START = /\bdocker\s+(?:compose\s+(?:-\S+\s+\S+\s+)*up|run)\b/;
const RECORDING = /RecordedReplies\b|WeirContract\.Rec|\bRec\(\s*"|\bRecorded[A-Z]\w*\s*[(.<]|fixtures[\\/]+recorded/;

/** The names inside one test file: its tests, its groups and (C#) its classes. */
export function namesOf(file, text) {
  const names = new Set();
  if (file.endsWith('.cs')) {
    for (const m of text.matchAll(CS_METHOD)) names.add(m[1]);
    for (const m of text.matchAll(CS_CLASS)) names.add(m[1]);
  } else if (file.endsWith('.ps1')) {
    for (const m of text.matchAll(/^\s*function\s+([\w-]+)/gim)) names.add(m[1]);
    for (const m of text.matchAll(/(['"])([a-z][a-z0-9-]*(?:\.[a-z0-9*-]+)+)\1/g)) names.add(m[2]);
  } else if (/\.ya?ml$/.test(file)) {
    for (const m of text.matchAll(/^\s*-\s+name:\s*(.+?)\s*$/gm)) names.add(m[1].replace(/^["']|["']$/g, ''));
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

  /**
   * Is the proof a step of a packaging workflow (or a packaging script) that starts the shipped container image?
   * Decided statically from the step's text: a named step is judged alone, a script or "*" as a whole. { real, why }.
   */
  containerSmoke(file, name) {
    const f = this.files.get(file);
    if (!f || !CONTAINER_SMOKE_FILE.test(file)) return { real: false, why: 'not a packaging workflow or container script' };
    let text = f.text;
    if (/\.ya?ml$/.test(file) && name && name !== '*') {
      const q = squash(name);
      const step = f.text.split(/^(?=\s*-\s+name:)/m).find((x) => squash(/^\s*-\s+name:\s*(.+)/.exec(x)?.[1] ?? '') === q);
      if (step === undefined) return { real: false, why: `no step named "${name}" to judge` };
      text = step;
    }
    return CONTAINER_START.test(text) ? { real: true, why: '' } : { real: false, why: `${name || file} does not start the container image with docker run or docker compose up` };
  }

  /**
   * Is the test a real-fs test: it runs Deluno's real code against real files in a real temp folder, or a real SQLite file?
   * Decided statically from the class setup (the text before its first test) plus the test's own body.
   * A named test is judged alone; a class name, a cited fragment or "*" is judged over every test it covers, and every one must qualify.
   * { real, why }: what cannot be decided is not real.
   */
  realFs(file, name) {
    const f = this.files.get(file);
    if (!f || !file.endsWith('.cs')) return { real: false, why: 'not a C# test file' };
    const header = f.header ?? '';
    let scope;
    const body = name && name !== '*' ? f.bodies?.get(name) : undefined;
    if (body !== undefined) scope = [[name, body]];
    else {
      const all = [...(f.bodies ?? [])];
      if (!name || name === '*' || f.names.has(name)) scope = all;
      else { const q = squash(name); scope = all.filter(([n]) => squash(n).startsWith(q)); }
    }
    if (scope.length === 0) return { real: false, why: 'no test found to judge' };
    for (const [n, text] of scope) {
      const both = header + '\n' + text;
      if (FS_FAKE.test(both)) return { real: false, why: `${n} uses a fake or in-memory file system or store` };
      if (!FS_CREATE.test(both)) return { real: false, why: `${n} (or its class setup) creates no real temp folder or database file` };
      if (!FS_WORK.test(both)) return { real: false, why: `${n} does no file or database work that can be seen` };
    }
    return { real: true, why: '' };
  }

  /**
   * Is a smoke test's API stubbed? Decided from the spec's text: a test is stubbed when its body, a beforeEach/beforeAll hook of the file,
   * or a helper function of the file that it calls intercepts requests (page.route, route.fulfill, serveLive*).
   * { stubbed, decided }: what cannot be decided (the test is not found, or the name is the whole file) counts as stubbed and not decided.
   */
  stubInfo(file, name) {
    const f = this.files.get(file);
    if (!f || !/\.spec\.ts$/.test(file)) return { stubbed: true, decided: false };
    f.stub ??= analyseSpec(f.text);
    const a = f.stub;
    if (!name || name === '*') return { stubbed: true, decided: false };
    let body = a.bodies.get(name);
    if (body === undefined) {
      const q = squash(name);
      for (const [title, text] of a.bodies) {
        const s = squash(title);
        if (s === q || (q.length >= 14 && (s.startsWith(q) || (s.length >= 14 && q.startsWith(s))))) { body = text; break; }
      }
    }
    if (body === undefined) return { stubbed: true, decided: false };
    const stubbed = a.hooksStub || STUB_MARKER.test(body) || a.stubHelpers.some((h) => new RegExp(`\\b${h}\\s*\\(`).test(body));
    return { stubbed, decided: true };
  }
}

const STUB_MARKER = /\.route\(|\bserveLive\w*|\.fulfill\(|routeFromHAR/;
/** Splits a Playwright spec into its tests and hooks and says which intercept requests. */
function analyseSpec(text) {
  const starts = [...text.matchAll(/^[ \t]*(?:(?:test|it)(?:\.(?:only|skip|fixme|slow|fail))?\(\s*(['"`])((?:\\.|(?!\1)[^\\\n])+)\1|test\.(beforeEach|beforeAll)\()/gm)];
  const header = starts.length ? text.slice(0, starts[0].index) : text;
  const bodies = new Map();
  let hooksStub = false;
  for (let i = 0; i < starts.length; i++) {
    const seg = text.slice(starts[i].index, i + 1 < starts.length ? starts[i + 1].index : text.length);
    if (starts[i][3]) { if (STUB_MARKER.test(seg)) hooksStub = true; } else bodies.set(starts[i][2], seg);
  }
  const stubHelpers = [];
  const defs = [...header.matchAll(/(?:async\s+)?function\s+(\w+)|(?:const|let)\s+(\w+)\s*=\s*(?:async\s*)?(?:function|\()/g)];
  for (let i = 0; i < defs.length; i++) {
    const seg = header.slice(defs[i].index, i + 1 < defs.length ? defs[i + 1].index : header.length);
    if (STUB_MARKER.test(seg)) stubHelpers.push(defs[i][1] ?? defs[i][2]);
  }
  return { bodies, hooksStub, stubHelpers };
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
  if (/^tests\/e2e-windows\/.+\.ts$/.test(file)) return 'windows-e2e';
  if (/^tests\/e2e\/.+\.ts$/.test(file)) return 'e2e';
  if (/^apps\/web\/tests\/.+\.spec\.ts$/.test(file)) return 'smoke';
  if (/\.test\.tsx?$/.test(file)) return 'vitest';
  if (index && index.replaysRecording(file, name)) return 'recording-replay';
  if (index && index.realFs(file, name).real) return 'real-fs';
  return 'backend';
}
