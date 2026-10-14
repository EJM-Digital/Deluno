import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { currentManifest, problems, hashOf, defaultRoot, manifestName } from './check-recorded-manifest.mjs';

function folder(files) {
  const root = mkdtempSync(join(tmpdir(), 'recorded-manifest-'));
  for (const [name, text] of Object.entries(files)) {
    mkdirSync(join(root, name, '..'), { recursive: true });
    writeFileSync(join(root, name), text);
  }
  return root;
}

test('a folder that matches its manifest passes, and a change, an addition or a removal fails and is named', () => {
  const root = folder({ '1.0.0/a.json': '{"a":1}\n', '1.0.0/b.json': '{"b":2}\n' });
  try {
    const manifest = join(root, manifestName);
    writeFileSync(manifest, currentManifest(root));
    assert.deepEqual(problems(root, manifest), []);

    writeFileSync(join(root, '1.0.0/a.json'), '{"a":2}\n');
    assert.match(problems(root, manifest).join('\n'), /1\.0\.0\/a\.json changed/);

    writeFileSync(join(root, '1.0.0/a.json'), '{"a":1}\n');
    writeFileSync(join(root, '1.0.0/c.json'), '{}\n');
    assert.match(problems(root, manifest).join('\n'), /1\.0\.0\/c\.json is not in the manifest/);

    rmSync(join(root, '1.0.0/c.json'));
    rmSync(join(root, '1.0.0/b.json'));
    assert.match(problems(root, manifest).join('\n'), /1\.0\.0\/b\.json is in the manifest but the file is gone/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('line endings do not change a hash, so a Windows and a Linux checkout agree', () => {
  const root = folder({ 'v/lf.json': '{\n  "a": 1\n}\n', 'v/crlf.json': '{\r\n  "a": 1\r\n}\r\n' });
  try { assert.equal(hashOf(join(root, 'v/lf.json')), hashOf(join(root, 'v/crlf.json'))); } finally { rmSync(root, { recursive: true, force: true }); }
});

test('a missing manifest is a problem that says how to write it', () => {
  const root = folder({ 'v/a.json': '{}\n' });
  try { assert.match(problems(root, join(root, manifestName))[0], /--write/); } finally { rmSync(root, { recursive: true, force: true }); }
});

test("Deluno's recordings in the repository match their manifest", () => {
  assert.deepEqual(problems(defaultRoot, join(defaultRoot, manifestName)), []);
});
