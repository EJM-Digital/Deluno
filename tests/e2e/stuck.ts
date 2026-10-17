import fs from "node:fs";
import path from "node:path";
import { expect } from "@playwright/test";
import type { Sent } from "./flow.ts";
import type { Stack } from "./stack.ts";
import { sleep } from "./flow.ts";

/**
 * What the `stuck` groups share (scripts/golden-path/suite/vm/scenarios/G05-Stuck.ps1, #1206, #1225): downloads stuck at 0 bytes,
 * stuck just short of the end, too slow and with too few peers. Each is struck at its count, the plural wording is right, and a
 * download stuck at 99.99% never reads 100% or "Not handed over".
 *
 * Deluno gives a download one rule's strike at a time, so the rules are set per phase: "No progress" (two strikes) in phase A;
 * "Too few peers" (two strikes, floor 5) and "Too slow" (one strike, 2 MB/s floor) in phase B, "No progress" off. The rule's own
 * check-in timer is one minute for the test, as on the VM, and the rules are put back as they were at the end.
 */

type Rule = Record<string, any>;

const RULE_KINDS = ["no-progress", "too-slow", "low-peers", "dead-torrent", "magnet-metadata", "client-failure"];

export const readRules = (stack: Stack) => stack.deluno.must<Rule[]>("GET", "/api/download-health/rules");

/** Turns exactly the named rules on (strikes, floor and check-in timer), every other rule off (Set-HealthRules). */
export async function setHealthRules(stack: Stack, on: Record<string, { strikes: number; threshold?: number }>, checkMinutes = 1) {
  const rules = await readRules(stack);
  for (const kind of RULE_KINDS) {
    const rule = rules.find((r) => r.kind === kind);
    const want = kind in on;
    if (!rule && !want) continue;
    const base: Rule = rule ?? {
      id: "new", kind, enabled: false, strikeLimit: 3, timerMinutes: 5, appliesTo: "both", minCompletionPercent: 0, maxCompletionPercent: 100,
      resetStrikesOnProgress: true, actionPreset: "replace", threshold: 0, privacyScope: "both", tooSlowMaxTotalMinutes: null, ignoreAboveSizeBytes: null,
      skipWhileAlternateSpeedLimitOn: false, resetStrikesMinimumProgressBytes: null
    };
    const body: Rule = { ...base, enabled: want };
    if (want) {
      body.strikeLimit = on[kind].strikes;
      body.timerMinutes = checkMinutes;
      body.actionPreset = "replace";
      if (on[kind].threshold !== undefined) body.threshold = on[kind].threshold;
    }
    await stack.deluno.must("PUT", `/api/download-health/rules/${base.id}`, body);
  }
}

export async function restoreRules(stack: Stack, original: Rule[]) {
  for (const rule of original) {
    try { await stack.deluno.must("PUT", `/api/download-health/rules/${rule.id}`, rule); } catch (error) { console.log(`could not restore rule ${rule.kind}: ${error}`); }
  }
}

/** Every client's queue rows as Deluno reads them (GET telemetry), flattened. */
async function telemetryRows(stack: Stack): Promise<any[]> {
  const overview = await stack.deluno.must<any>("GET", "/api/download-clients/telemetry");
  return (overview.clients ?? []).flatMap((c: any) => (c.queue ?? []).filter(Boolean));
}

/** The newest page of Activity as lines of JSON (as the person reads it), not lower-cased. */
async function recentActivity(stack: Stack): Promise<string[]> {
  const page = await stack.deluno.must<any>("GET", "/api/activity?pageSize=300");
  return ((Array.isArray(page) ? page : page.items) as any[]).map((item) => JSON.stringify(item));
}

export interface Seen { maxProgress: number; statuses: Set<string>; line?: string; seconds?: number; lastRow?: any }

/** Waits for each film to be struck (or the time to run out) and records what each looked like on the way. */
export async function waitAllStruck(stack: Stack, films: Map<string, Sent>, timeoutSec = 420): Promise<Map<string, Seen>> {
  const seen = new Map<string, Seen>();
  for (const key of films.keys()) seen.set(key, { maxProgress: 0, statuses: new Set() });
  const started = Date.now();
  while (Date.now() - started < timeoutSec * 1000) {
    const rows = await telemetryRows(stack).catch(() => [] as any[]);
    const recent = await recentActivity(stack).catch(() => [] as string[]);
    for (const [key, sent] of films) {
      const s = seen.get(key)!;
      if (s.line) continue;
      const { film } = sent;
      const row = rows.find((r) => String(r.releaseName ?? "").includes(film.release) || String(r.title ?? "").includes(film.title));
      if (row) {
        s.lastRow = row;
        if (Number(row.progress) > s.maxProgress) s.maxProgress = Number(row.progress);
        s.statuses.add(String(row.status));
      }
      const line = recent.find((l) => l.toLowerCase().includes(film.title.toLowerCase()) && /Fixed a stuck download/i.test(l));
      if (line) { s.line = line; s.seconds = Math.round((Date.now() - sent.sentAt) / 1000); }
    }
    if ([...seen.values()].every((s) => s.line)) break;
    await sleep(4000);
  }
  return seen;
}

function saveEvidence(name: string, text: string) {
  const dir = path.resolve(process.cwd(), "artifacts/e2e/evidence");
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, name), text);
}

/**
 * What a strike line says (#1206): "had fewer than 2 peers for 3 checks in a row", "stayed under 75 KB/s for 3 checks", "made no
 * progress for 3 checks in a row". The speed rule counts checks without "in a row", so the suite asks for that wording from the slow
 * rule and "in a row" from the others. 1 check is singular, N checks plural.
 */
export function assertStruck(sent: Sent, seen: Seen | undefined, strikes: number, ruleWords: RegExp, opts: { shortOfEnd?: boolean; inARow?: boolean } = {}) {
  const inARow = opts.inARow ?? true;
  expect.soft(!!seen?.line, `It was struck and replaced (${seen?.line ? `after ${seen.seconds} s` : 'no "Fixed a stuck download" line in Activity'})`).toBe(true);
  if (seen?.line) {
    expect.soft(ruleWords.test(seen.line), `The line names the rule (${ruleWords})`).toBe(true);
    const m = /for (\d+) checks?/.exec(seen.line);
    expect.soft(m ? Number(m[1]) : -1, `It was struck at its count (${strikes})${m ? `: ${m[0]}` : ": no count in the line"}`).toBe(strikes);
    const unit = strikes === 1 ? "check" : "checks";
    const words = new RegExp(`for ${strikes} ${unit}${inARow ? " in a row" : ""}(?![\\w])`, "i");
    expect.soft(words.test(seen.line), `The wording is right for ${strikes} (1 check, ${strikes} checks${inARow ? " in a row" : ""})${m ? `: ${m[0]}` : ""}`).toBe(true);
    expect.soft(/looking for another copy/i.test(seen.line) && /removed it from/i.test(seen.line), "Deluno removed it from the client and looked for another copy").toBe(true);
    saveEvidence(`strike-${sent.film.title}.txt`, seen.line);
  }
  if (opts.shortOfEnd && seen) {
    expect.soft(seen.maxProgress > 0 && seen.maxProgress < 100, `Its progress read below 100% all the way, never rounded up to 100% (highest progress seen: ${seen.maxProgress})`).toBe(true);
    const bad = [...seen.statuses].filter((s) => /notHandedOver|not-handed/i.test(s));
    expect.soft(bad, `It was never shown as "Not handed over" (statuses seen: ${[...seen.statuses].sort().join(", ")})`).toEqual([]);
    // The client's row as Deluno read it last, so a progress that never moves can be told from a row that was never found.
    if (seen.lastRow) saveEvidence(`row-${sent.film.title}.json`, JSON.stringify(seen.lastRow, null, 2));
  }
}
