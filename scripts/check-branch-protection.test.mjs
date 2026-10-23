// node --test scripts/check-branch-protection.test.mjs
// GAT-10: the rules guarding main and the v* tags, judged on the rulesets GitHub really sent (the recording) and on weakened copies.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

import { fetchRulesets, problems, recordedRulesets } from './check-branch-protection.mjs';

const recording = fileURLToPath(new URL('../tests/fixtures/recorded/github/2022-11-28/repository-rulesets.json', import.meta.url));
const real = () => structuredClone(recordedRulesets(recording));
const named = (rulesets, name) => rulesets.find((r) => r.name === name);
const withoutRules = (ruleset, ...types) => { ruleset.rules = ruleset.rules.filter((rule) => !types.includes(rule.type)); };

test('the rules GitHub really sent are in place', () => {
  assert.equal(real().length, 3);
  assert.deepEqual(problems(real()), []);
});

test('a reply without the bypass list is judged on everything else', () => {
  const rulesets = real();
  for (const r of rulesets) delete r.bypass_actors;
  assert.deepEqual(problems(rulesets), []);
  withoutRules(named(rulesets, 'main is protected'), 'required_status_checks');
  assert.equal(problems(rulesets).length, 1);
});

test('main without the ci-passed requirement is refused, and so is a different check', () => {
  let rulesets = real();
  withoutRules(named(rulesets, 'main is protected'), 'required_status_checks');
  assert.match(problems(rulesets).join('\n'), /ci-passed/);
  rulesets = real();
  named(rulesets, 'main is protected').rules.find((r) => r.type === 'required_status_checks').parameters.required_status_checks = [{ context: 'build' }];
  assert.match(problems(rulesets).join('\n'), /ci-passed/);
});

test('letting anyone but the admin role bypass the main rule is refused', () => {
  const rulesets = real();
  named(rulesets, 'main is protected').bypass_actors = [{ actor_id: 4, actor_type: 'RepositoryRole', bypass_mode: 'always' }];
  assert.match(problems(rulesets).join('\n'), /ci-passed/);
});

test('main that can be deleted or rewritten is refused', () => {
  for (const type of ['deletion', 'non_fast_forward']) {
    const rulesets = real();
    withoutRules(named(rulesets, 'main is protected'), type);
    withoutRules(named(rulesets, 'main is never rewritten or deleted'), type);
    const found = problems(rulesets).join('\n');
    assert.match(found, /ci-passed/);
    assert.match(found, /rewritten or deleted/);
  }
});

test('the never-rewritten rule with someone allowed to bypass it is refused', () => {
  const rulesets = real();
  named(rulesets, 'main is never rewritten or deleted').bypass_actors = [{ actor_id: 5, actor_type: 'RepositoryRole', bypass_mode: 'always' }];
  // The first rule also blocks rewriting but lets the admin role bypass it, so only the "nobody" rule is gone.
  assert.match(problems(rulesets).join('\n'), /rewritten or deleted/);
});

test('a rule that only evaluates, or is turned off, protects nothing', () => {
  for (const enforcement of ['evaluate', 'disabled']) {
    const rulesets = real();
    named(rulesets, 'main is protected').enforcement = enforcement;
    const found = problems(rulesets).join('\n');
    assert.match(found, /ci-passed/);
    assert.match(found, /not active/);
  }
});

test('a rule that no longer covers main is refused', () => {
  const moved = real();
  named(moved, 'main is protected').conditions.ref_name.include = ['refs/heads/develop'];
  assert.match(problems(moved).join('\n'), /ci-passed/);
  const excluded = real();
  named(excluded, 'main is protected').conditions.ref_name.exclude = ['refs/heads/main'];
  assert.match(problems(excluded).join('\n'), /ci-passed/);
});

test('release tags anyone can make, move or delete are refused', () => {
  for (const type of ['creation', 'update', 'deletion']) {
    const rulesets = real();
    withoutRules(named(rulesets, 'only the owner makes release tags'), type);
    assert.match(problems(rulesets).join('\n'), /release tag/);
  }
  assert.match(problems(real().filter((r) => r.target !== 'tag')).join('\n'), /release tag/);
  const wrong = real();
  named(wrong, 'only the owner makes release tags').conditions.ref_name.include = ['refs/tags/old-*'];
  assert.match(problems(wrong).join('\n'), /release tag/);
});

test('no rulesets at all gives all three problems', () => {
  assert.equal(problems([]).length, 3);
});

test('every ruleset is fetched in full, and a reply that is not a list is refused', async () => {
  const { exchanges } = JSON.parse(readFileSync(recording, 'utf8'));
  const asked = [];
  const fetchJson = async (path) => {
    asked.push(path);
    const hit = exchanges.find((e) => e.request.path === `api.github.com${path}`);
    assert.ok(hit, `no recorded reply for ${path}`);
    return hit.reply.body;
  };
  const full = await fetchRulesets('EJM-Digital/Deluno', fetchJson);
  assert.equal(full.length, 3);
  assert.equal(asked.length, 4);
  assert.deepEqual(problems(full), []);
  await assert.rejects(() => fetchRulesets('x/y', async () => ({ message: 'Not Found' })), /not a list/);
});
