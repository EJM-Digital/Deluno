// Run: node --test scripts/check-recording-freshness.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { check, latestRelease, newestRecorded, recordedApps, PROJECTS } from './check-recording-freshness.mjs';

test('the newest recorded version of an app is the highest, not the last listed', () => {
  assert.equal(newestRecorded(['5.2.4', '4.6.7', '5.2.1']), '5.2.4');
  assert.equal(newestRecorded(['4.10.1.0']), '4.10.1.0');
  assert.equal(newestRecorded([]), null);
});

test('a recording a major version or more than one minor version behind is stale, the rest are ok, and unknown apps are said so', async () => {
  const apps = [
    { app: 'qbittorrent', versions: ['4.6.7', '5.2.4'] },
    { app: 'sabnzbd', versions: ['5.1.3'] },
    { app: 'radarr', versions: ['6.4.4.10685'] },
    { app: 'transmission', versions: ['4.1.3'] },
    { app: 'mystery', versions: ['1.0.0'] },
    { app: 'tmdb', versions: ['2026-10-18'] },
    { app: 'weir', versions: ['1.0.0-rc.18'] }
  ];
  const newest = { 'qbittorrent/qBittorrent': 'release-5.2.5', 'sabnzbd/sabnzbd': '6.0.0', 'Radarr/Radarr': 'v6.6.0.1', 'transmission/transmission': (() => { throw new Error('GitHub answered 403'); }) };
  const rows = await check(apps, async (project) => { const v = newest[project]; return typeof v === 'string' ? v : v(); });
  assert.deepEqual(rows.map((r) => `${r.app}:${r.state}`), ['qbittorrent:ok', 'sabnzbd:stale', 'radarr:stale', 'transmission:unreadable', 'mystery:not-checked']);
  assert.equal(rows[1].recorded, '5.1.3');
  assert.equal(rows[1].latest, '6.0.0');
});

test('every app folder under tests/fixtures/recorded is either checked, a service, or named in the table', () => {
  const root = mkdtempSync(join(tmpdir(), 'fresh-'));
  try {
    mkdirSync(join(root, 'sabnzbd', '5.1.3'), { recursive: true });
    mkdirSync(join(root, 'tmdb', '2026-10-18'), { recursive: true });
    assert.deepEqual(recordedApps(root).map((a) => a.app).sort(), ['sabnzbd', 'tmdb']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
  const real = recordedApps().map((a) => a.app);
  assert.ok(real.includes('qbittorrent'));
  for (const app of ['qbittorrent', 'sabnzbd', 'radarr']) assert.ok(PROJECTS[app], app);
});

test('the newest release is read from the project\'s latest release, and a refusal is said in words', async () => {
  const ok = await latestRelease('a/b', async () => ({ ok: true, json: async () => ({ tag_name: 'v1.2.3' }) }));
  assert.equal(ok, 'v1.2.3');
  await assert.rejects(latestRelease('a/b', async () => ({ ok: false, status: 404 })), /a\/b: GitHub answered 404/);
});
