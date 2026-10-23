// The Home Assistant recipe (integrations/home-assistant/deluno.yaml) says what the README and the API document: the five sections Home
// Assistant loads are all there, each rest_command calls the route it names with the method and body the server takes, and every call
// carries the key from the secrets file (coverage register, engines). Read as text: the recipe uses Home Assistant's own !secret tag.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const recipe = readFileSync(join(root, 'integrations', 'home-assistant', 'deluno.yaml'), 'utf8').replace(/\r\n/g, '\n');

/** The indented lines under a top-level key, up to the next top-level key. */
const section = (name) => {
  const start = recipe.split('\n').findIndex((line) => line === `${name}:`);
  assert.ok(start >= 0, `${name}: is missing from the recipe`);
  const lines = recipe.split('\n').slice(start + 1);
  const end = lines.findIndex((line) => /^[A-Za-z_]/.test(line));
  return (end < 0 ? lines : lines.slice(0, end)).join('\n');
};

/** One rest_command's lines. */
const command = (name) => {
  const lines = section('rest_command').split('\n');
  const start = lines.findIndex((line) => line === `  ${name}:`);
  assert.ok(start >= 0, `rest_command ${name} is missing`);
  const rest = lines.slice(start + 1);
  const end = rest.findIndex((line) => /^ {2}[a-z_]+:/.test(line));
  return (end < 0 ? rest : rest.slice(0, end)).join('\n');
};

test('the recipe has every section Home Assistant loads: input_text, sensor, template, rest_command and automation', () => {
  for (const name of ['input_text', 'sensor', 'template', 'rest_command', 'automation']) {
    assert.ok(section(name).trim().length > 0, `${name} has nothing under it`);
  }
  assert.match(section('input_text'), /deluno_api_url:/);
  assert.match(section('sensor'), /resource: ".*\/api\/v1\/automation\/summary"/);
  assert.match(section('template'), /unique_id: deluno_queue_active/);
});

test('every rest_command calls its route under /api/v1 with the method the server takes and the key from the secrets file', () => {
  const expected = {
    deluno_search_library: ['POST', '/libraries/{{ library_id }}/search-now'],
    deluno_pause_automation: ['PUT', '/settings/automation'],
    deluno_resume_automation: ['PUT', '/settings/automation'],
    deluno_pause_existing_import: ['POST', '/libraries/{{ library_id }}/import-existing/pause'],
    deluno_resume_existing_import: ['POST', '/libraries/{{ library_id }}/import-existing/resume'],
    deluno_approve_intake_preview: ['POST', '/intake-sources/{{ source_id }}/approve-preview']
  };
  for (const [name, [method, route]] of Object.entries(expected)) {
    const body = command(name);
    assert.ok(body.includes(`url: "{{ states('input_text.deluno_api_url') }}/api/v1${route}"`), `${name} calls ${route}`);
    assert.match(body, new RegExp(`method: ${method}\\b`), `${name} uses ${method}`);
    assert.match(body, /X-Api-Key: !secret deluno_api_key/, `${name} sends the key from the secrets file`);
    assert.match(body, /Content-Type: application\/json/, `${name} says its body is JSON`);
  }
});

test('pausing and resuming automation send {"isEnabled": false} and {"isEnabled": true}, the body the settings route reads', () => {
  assert.match(command('deluno_pause_automation'), /payload: '\{"isEnabled": false\}'/);
  assert.match(command('deluno_resume_automation'), /payload: '\{"isEnabled": true\}'/);
});

test('approving an intake preview sends the entries and whether to search after adding, as the route reads them', () => {
  const body = command('deluno_approve_intake_preview');
  assert.match(body, /"entries": entries/);
  assert.match(body, /"searchAfterAdd": search_after_add/);
  assert.match(body, /to_json/);
});
