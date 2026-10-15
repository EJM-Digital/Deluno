// Run: node --test scripts/check-real-specs.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { stubsIn, violations } from './check-real-specs.mjs';

function repo(files) {
  const root = mkdtempSync(join(tmpdir(), 'real-specs-'));
  for (const [rel, text] of Object.entries(files)) {
    const full = join(root, rel);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, text);
  }
  return root;
}

test('every way of faking a reply is found', () => {
  for (const text of ['await page.route("**/api/x", r => r.fulfill({}))', 'context.route("**", h)', 'await page.unroute("**")', 'page.routeFromHAR("a.har")', 'serveLiveMovies(page)', 'window.fetch = () => 1', 'page.routeWebSocket("ws://x", h)']) {
    assert.ok(stubsIn(text).length > 0, text);
  }
});

test('a comment that names a stub is not a stub, and a comparison is not an assignment', () => {
  assert.deepEqual(stubsIn('// no page.route here\n/* serveLiveX */ const a = window.fetch === undefined;'), []);
});

test('a real spec that stubs is reported, and one that does not is clean', () => {
  const dirty = repo({ 'apps/web/tests/real/a.spec.ts': 'await page.route("**", r => r.abort());' });
  assert.equal(violations(dirty).length, 1);
  const clean = repo({ 'apps/web/tests/real/a.spec.ts': 'await page.goto("/movies");', 'apps/web/tests/smoke/b.spec.ts': 'await page.route("**", h);' });
  assert.deepEqual(violations(clean), []);
});

test('a stub hidden in a helper the real spec imports is reported against the spec', () => {
  const root = repo({
    'apps/web/tests/real/a.spec.ts': 'import { go } from "../helpers/go";',
    'apps/web/tests/helpers/go.ts': 'import { deep } from "./deep"; export const go = 1;',
    'apps/web/tests/helpers/deep.ts': 'export const deep = (p) => p.route("**", h);'
  });
  const found = violations(root);
  assert.equal(found.length, 1);
  assert.equal(found[0].file, 'apps/web/tests/real/a.spec.ts');
  assert.equal(found[0].via, 'apps/web/tests/helpers/deep.ts');
});

test('a skipped real spec must say why in words', () => {
  assert.equal(violations(repo({ 'apps/web/tests/real/a.spec.ts': 'test.fixme(true);' })).length, 1);
  assert.deepEqual(violations(repo({ 'apps/web/tests/real/a.spec.ts': 'test.fixme(true, "the filter menu has no accessible name, see the audit");' })), []);
});
