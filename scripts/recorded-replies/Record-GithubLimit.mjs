// Records GitHub's real reply once the anonymous hourly allowance of api.github.com is used up (#1181, #1237).
//
// It makes unauthenticated GETs of one small public repository until GitHub answers that none are left, then writes that answer
// to tests/fixtures/recorded/github/2022-11-28/rate-limit-used-up.json. It spends the allowance of the address it runs from (60 calls an
// hour, shared by every program on that address), so run it from a machine that can do without GitHub's API for an hour. The calls stop
// at the first answer that says none are left, and never exceed 75.
//
// The caller's own address appears in GitHub's message ("API rate limit exceeded for <address>"); it is replaced by a documentation
// address (203.0.113.7), the only edit made to the reply. Usage: node scripts/recorded-replies/Record-GithubLimit.mjs
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const target = path.resolve(here, "../../tests/fixtures/recorded/github/2022-11-28/rate-limit-used-up.json");
const URI = "https://api.github.com/repos/octocat/Hello-World";
const KEEP = ["content-type", "x-ratelimit-limit", "x-ratelimit-remaining", "x-ratelimit-reset", "x-ratelimit-resource", "x-ratelimit-used"];

let calls = 0;
for (; calls < 75;) {
  calls++;
  const response = await fetch(URI, { headers: { "User-Agent": "Deluno-test-recording", Accept: "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28" } });
  const text = await response.text();
  if (response.status !== 403 && response.status !== 429) {
    if (response.headers.get("x-ratelimit-remaining") === "0") console.log(`call ${calls}: that was the last one`);
    continue;
  }
  const headers = {};
  for (const name of KEEP) { const value = response.headers.get(name); if (value !== null) headers[name] = value; }
  const body = JSON.parse(text);
  body.message = String(body.message).replace(/for \S+\./, "for 203.0.113.7.");
  const recording = {
    app: "github",
    version: "2022-11-28",
    state: "rate-limit-used-up",
    recordedAt: new Date().toISOString(),
    note: `A real unauthenticated GET of ${URI.replace("https://", "")} after ${calls - 1} earlier unauthenticated calls from the same address spent the hourly allowance (the REST API's primary rate limit). The caller's address in the message was replaced by a documentation address; nothing else was changed. The reset time is a real one from that moment: a test that needs a limit that has not lifted yet replaces X-RateLimit-Reset, never anything else.`,
    exchanges: [{ request: { method: "GET", path: URI.replace("https://", ""), body: null }, reply: { status: response.status, headers, body } }]
  };
  mkdirSync(path.dirname(target), { recursive: true });
  writeFileSync(target, JSON.stringify(recording, null, 2) + "\n");
  console.log(`recorded after ${calls} calls: ${response.status} ${JSON.stringify(headers)}`);
  process.exit(0);
}
console.error(`GitHub never said the allowance was used up after ${calls} calls (this address may be authenticated or allowed more than 60 an hour).`);
process.exit(1);
