import { expect, test } from "@playwright/test";
import { setUpStack, until, type Stack } from "./stack.ts";

/**
 * Group `nzbgetsetup` (scripts/golden-path/suite/vm/scenarios/G00-NzbgetSetupTwice.ps1, #1257): the Usenet provider set a second time on an
 * NZBGet that already has it. Deluno once read NZBGet's settings with its `config` call, which adds NZBGet's own runtime-only values
 * (ConfigFile, AppBin, AppDir, Version), and saved the whole list back; on the second run NZBGet logged `Invalid option "ConfigFile"` and
 * "Pausing all activities due to errors in configuration", and every download sat at 0 bytes. This sets the provider twice through Deluno's
 * real API (the call the guided setup and the Connections screen make) and then asks NZBGet itself: not paused, no `Invalid option`, no
 * `errors in configuration` in its log, and Deluno shows the client without a settings-error warning.
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;

/** What is wrong with NZBGet, in words, from its own `status` and `log` replies (empty when nothing). */
function nzbgetConfigProblems(status: { DownloadPaused?: boolean } | undefined, log: Array<{ Text?: string }>): string[] {
  const problems: string[] = [];
  if (status?.DownloadPaused) problems.push("NZBGet has all downloads paused (DownloadPaused is true).");
  for (const entry of log) {
    const text = String(entry.Text ?? "");
    if (/Invalid option/.test(text) || /errors in configuration/.test(text)) problems.push(`NZBGet's log: ${text}`);
  }
  return problems;
}

const provider = () => ({ host: "fixture", port: 1119, useSsl: false, username: "golden", password: `Gp-${Math.random().toString(16).slice(2, 14)}`, connections: 2 });

test.beforeAll(async () => {
  test.setTimeout(10 * 60_000);
  stack = await setUpStack();
});

test("nzbget.setup-twice: The Usenet provider set again on an NZBGet that already has it leaves a valid config: not paused, no \"Invalid option\" in its log", async () => {
  const client = (await stack.deluno.must<any[]>("GET", "/api/download-clients")).find((c) => c.protocol === "nzbget");
  expect(client, "Deluno has an NZBGet connection").toBeTruthy();

  // The first setup, as the environment's setup does it, so the second is the "set it again" case.
  const first = await stack.deluno.must<any>("POST", "/api/download-clients/usenet-provider", provider());
  console.log("first setup: " + JSON.stringify(first));
  expect(first.saved, "The first setup saved the provider").toBe(true);
  // NZBGet reloads after the save; it is ready again when it answers with the new login and is not paused.
  await until("NZBGet to be back after the first setup", 120, async () => {
    const config = await stack.nzbget.call("config") as Array<{ Name: string; Value: string }>;
    const s = await stack.nzbget.call("status");
    return config.find((c) => c.Name === "Server1.Username")?.Value && s && !s.DownloadPaused ? s : undefined;
  }, 3000);
  await new Promise((resolve) => setTimeout(resolve, 5000));

  // Before: the first setup left NZBGet clean (otherwise this scenario would blame the second one for the first's damage).
  const before = nzbgetConfigProblems(await stack.nzbget.call("status"), await stack.nzbget.call("log", [0, 500]));
  expect.soft(before, "After the first setup NZBGet is not paused and its log is clean").toEqual([]);

  const set = await stack.deluno.must<any>("POST", "/api/download-clients/usenet-provider", provider());
  const mine = (set.clients as any[]).find((c) => c.clientId === client.id);
  expect.soft(set.saved && mine?.saved, `Deluno gave the provider to NZBGet a second time${mine ? ` (${mine.message})` : " (NZBGet is not in the answer)"}`).toBeTruthy();

  // NZBGet reads its settings a moment after the reload.
  const answering = await until("NZBGet to answer with its downloads not paused", 90, async () => {
    const s = await stack.nzbget.call("status");
    return s && !s.DownloadPaused ? s : undefined;
  }, 3000).catch(() => undefined);
  expect.soft(answering, "NZBGet answers and its downloads are not paused").toBeTruthy();
  await new Promise((resolve) => setTimeout(resolve, 5000));
  const log = await stack.nzbget.call("log", [0, 500]) as Array<{ Time: number; Text: string }>;
  const after = nzbgetConfigProblems(await stack.nzbget.call("status"), log);
  expect.soft(after, "After the second setup NZBGet's status is not paused and its log has no \"Invalid option\" and no \"errors in configuration\"").toEqual([]);

  const row = (await stack.deluno.must<any[]>("GET", "/api/download-clients")).find((c) => c.id === client.id);
  expect.soft(String(row?.lastHealthMessage ?? ""), "Deluno shows NZBGet without a settings-error warning").not.toMatch(/settings have errors/);
});
