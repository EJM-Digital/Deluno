// A Playwright spec marked real runs against the real Deluno server with nothing stubbed (docs/testing/STRATEGY.md, measure 4).
//
//   node scripts/check-real-specs.mjs
//
// The mark is the folder: every spec under apps/web/tests/real/ is real. It may not intercept a request or fake a reply for the calls its
// behaviour depends on: no page.route, context.route, unroute, routeFromHAR, routeWebSocket, route.fulfill, serveLive*, and no
// replacing window.fetch or XMLHttpRequest in the page. The same goes for every helper it imports, read recursively, so a stub cannot
// hide one file away. Comments are ignored. A real spec marked test.fixme or test.skip must say why, in words on the same line: a
// skipped spec proves nothing, so the reason is the record of the defect that stops it.
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const REAL_DIR = 'apps/web/tests/real';

const STUBS = [
  [/\.route\s*\(/, 'page.route or context.route'],
  [/\.unroute\s*\(/, 'unroute'],
  [/\brouteFromHAR\b/, 'routeFromHAR'],
  [/\brouteWebSocket\b/, 'routeWebSocket'],
  [/\.fulfill\s*\(/, 'route.fulfill'],
  [/\bserveLive\w*/, 'serveLive*'],
  [/\b(?:window|globalThis|self)\.(?:fetch|XMLHttpRequest)\s*=[^=]/, 'a replaced window.fetch or XMLHttpRequest']
];

const stripComments = (text) => text.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/(^|[^:'"`])\/\/.*$/gm, '$1');

export function stubsIn(text) {
  const code = stripComments(text);
  return STUBS.filter(([re]) => re.test(code)).map(([, why]) => why);
}

/** The local files a spec imports (relative paths only), resolved to files. */
export function localImports(file, text) {
  const out = [];
  for (const m of stripComments(text).matchAll(/(?:from|import)\s*\(?\s*["'](\.{1,2}\/[^"']+)["']/g)) {
    const base = resolve(dirname(file), m[1]);
    for (const candidate of [base, base + '.ts', base + '.tsx', join(base, 'index.ts')]) {
      if (existsSync(candidate) && statSync(candidate).isFile()) { out.push(candidate); break; }
    }
  }
  return out;
}

function walk(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(join(dir, e.name)) : [join(dir, e.name)]));
}

const norm = (root, f) => relative(root, f).replace(/\\/g, '/');

/** Every breach in the real specs under root: { file, via, why }. */
export function violations(root) {
  const found = [];
  for (const file of walk(join(root, REAL_DIR)).filter((f) => /\.spec\.ts$/.test(f))) {
    const seen = new Set();
    const visit = (f) => {
      if (seen.has(f)) return;
      seen.add(f);
      const text = readFileSync(f, 'utf8');
      for (const why of stubsIn(text)) found.push({ file: norm(root, file), via: norm(root, f), why });
      for (const next of localImports(f, text)) visit(next);
    };
    visit(file);
    const text = stripComments(readFileSync(file, 'utf8'));
    for (const m of text.matchAll(/test\.(?:fixme|skip)\s*\(([^\n]*)/g)) {
      if (!/["'`][^"'`]{12,}["'`]/.test(m[1])) found.push({ file: norm(root, file), via: norm(root, file), why: 'a skipped or fixme spec with no reason in words on the same line' });
    }
  }
  return found;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
  const found = violations(root);
  const count = walk(join(root, REAL_DIR)).filter((f) => /\.spec\.ts$/.test(f)).length;
  if (found.length) {
    console.error(`${found.length} breach(es) in the real specs (${REAL_DIR}); a real spec uses the real server for every call it depends on:`);
    for (const v of found) console.error(`  ${v.file}: ${v.why}${v.via !== v.file ? ` (in ${v.via})` : ''}`);
    process.exit(1);
  }
  console.log(`OK: ${count} real spec(s) under ${REAL_DIR} stub nothing.`);
}
