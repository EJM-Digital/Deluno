// The gate's own check of the metadata gateway runs every gateway test file and syntax-checks every gateway source file
// (coverage register, engines; limits.test.js and limits.js were once left out, so the budgets could break unseen).
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const gateway = join(root, 'services', 'metadata-gateway');
const ciCheck = readFileSync(join(root, 'scripts', 'ci-check.ps1'), 'utf8');

test('ci-check runs every gateway test file', () => {
  const tests = readdirSync(join(gateway, 'test')).filter((name) => name.endsWith('.test.js'));
  assert.ok(tests.length >= 4, 'the gateway has its test files');
  for (const name of tests) {
    assert.ok(ci_check_names(`services/metadata-gateway/test/${name}`), `${name} is not run by scripts/ci-check.ps1`);
  }
});

test('ci-check syntax-checks every gateway source file', () => {
  const sources = readdirSync(join(gateway, 'src')).filter((name) => name.endsWith('.js'));
  assert.ok(sources.length >= 4, 'the gateway has its source files');
  for (const name of sources) {
    assert.ok(ci_check_names(`services/metadata-gateway/src/${name}`), `${name} is not syntax-checked by scripts/ci-check.ps1`);
  }
});

function ci_check_names(path) {
  return ciCheck.includes(`"${path}"`);
}
