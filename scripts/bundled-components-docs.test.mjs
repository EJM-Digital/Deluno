// node --test scripts/bundled-components-docs.test.mjs
// docs/bundled-components-install.md and docs/guide-packages.md match behaviour (coverage register, guided-setup): every version
// the first names beside a pinned app is the pin in installer/bundled-components.json, every port it names beside an app is that
// app's default port, and every route either document names is one the code maps.
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const read = (...parts) => readFileSync(join(root, ...parts), 'utf8');
const manifest = JSON.parse(read('installer', 'bundled-components.json'));
const install = read('docs', 'bundled-components-install.md');
const guide = read('docs', 'guide-packages.md');

const names = { qbittorrent: 'qBittorrent', transmission: 'Transmission', deluge: 'Deluge', sabnzbd: 'SABnzbd', nzbget: 'NZBGet', weir: 'Weir' };
const components = Object.entries(names).map(([key, display]) => ({ key, display, ...manifest[key] }));
const escapeRegExp = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Every "<Name> 1.2.3" or "<Name> (Windows 1.2)" the text puts beside an app's name. */
export const versionsNamed = (text, display) => [...text.matchAll(new RegExp(`${display}\\*{0,2}\\s+\\(?(?:Windows\\s+)?v?(\\d+\\.\\d+(?:\\.\\d+)?)`, 'g'))].map((m) => m[1]);

test('every version the install guide names beside a pinned app is the version pinned for it', () => {
  const pinned = components.filter((c) => c.version && c.version !== 'latest');
  assert.ok(pinned.length >= 3);
  const wrong = [];
  for (const c of pinned) {
    for (const named of versionsNamed(install, c.display)) {
      if (!c.version.startsWith(named)) wrong.push(`${c.display} ${named} (pinned ${c.version})`);
    }
  }
  assert.deepEqual(wrong, []);
});

test('the version finder reads the shapes the guide uses and nothing else', () => {
  assert.deepEqual(versionsNamed('**qBittorrent 5.2.1** will not start', 'qBittorrent'), ['5.2.1']);
  assert.deepEqual(versionsNamed('**NZBGet (Windows 21.1)**: a switch', 'NZBGet'), ['21.1']);
  assert.deepEqual(versionsNamed('Transmission 4.1.1 MSI has', 'Transmission'), ['4.1.1']);
  assert.deepEqual(versionsNamed("the person's own qBittorrent on 8080", 'qBittorrent'), []);
});

test('the port the install guide names for Weir is the default port in the installer manifest', () => {
  const weir = manifest.weir;
  assert.ok(weir.defaultPort);
  assert.match(install, new RegExp(`port ${weir.defaultPort}\\b`));
  for (const c of components.filter((x) => x.defaultPort)) {
    for (const [, port] of install.matchAll(new RegExp(`${c.display}[^.\\n]{0,60}answers on (\\d{4,5})`, 'g'))) {
      assert.equal(Number(port), c.defaultPort, `${c.display} answers on ${port} in the guide`);
    }
  }
});

const sourceFiles = (folder) => readdirSync(folder, { recursive: true, withFileTypes: true })
  .filter((entry) => entry.isFile() && entry.name.endsWith('.cs'))
  .map((entry) => join(entry.parentPath, entry.name))
  .filter((file) => !/[\\/](bin|obj)[\\/]/.test(file));
const code = sourceFiles(join(root, 'src')).map((file) => readFileSync(file, 'utf8')).join('\n');

/** "GET /api/x/{id}/y" as the two documents write them, usually in backticks. */
export const routesNamed = (text) => [...text.matchAll(/(GET|POST|PUT|DELETE)\s+(\/api\/[A-Za-z0-9/{}\-_]+)/g)].map((m) => ({ method: m[1], path: m[2] }));

test('every route the two documents name is mapped by the code, under the verb they give it', () => {
  const routes = [...routesNamed(install), ...routesNamed(guide)];
  assert.ok(routes.length >= 8, `found only ${routes.length} routes`);
  const missing = [];
  for (const { method, path } of routes) {
    const literal = path.split('/').filter((part) => part && !part.startsWith('{')).slice(-1)[0];
    const verb = method[0] + method.slice(1).toLowerCase();
    const mapped = new RegExp(`Map${verb}\\(\\s*"[^"]*${escapeRegExp(literal)}[^"]*"`).test(code);
    if (!mapped) missing.push(`${method} ${path}`);
  }
  assert.deepEqual(missing, []);
});

test('the route finder reads a verb and a path out of prose', () => {
  assert.deepEqual(routesNamed('Call `POST /api/system/bundled-components/{kind}/connect` now, or GET /api/x.'), [
    { method: 'POST', path: '/api/system/bundled-components/{kind}/connect' }, { method: 'GET', path: '/api/x' }]);
});
