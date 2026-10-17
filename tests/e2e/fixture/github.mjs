// The stack's stand-in for api.github.com when GitHub's hourly allowance is used up (group `github`, #1181).
//
// What is real and what is not:
//   * REAL: the reply. Every request to api.github.com is answered with GitHub's own recorded reply to an unauthenticated call after
//     the hourly allowance was spent (tests/fixtures/recorded/github/2022-11-28/rate-limit-used-up.json, recorded by
//     scripts/recorded-replies/Record-GithubLimit.mjs from the real api.github.com): the same status, headers and body.
//   * NOT RECORDED: one value. The recorded X-RateLimit-Reset is a time that has long passed by the day the test runs, and a limit
//     that has already lifted is not the case under test, so the stand-in sends a reset an hour after it started. /limit says which.
//   * NOT GITHUB: the server. It sits behind the name api.github.com on the stack's own network, with a certificate from an authority the
//     stack made (make-certs.sh) and Deluno trusts. The stack's other GitHub names (github.com, codeload.github.com) are a container
//     with nothing listening, as the VM's hosts file did.
import fs from "node:fs";
import http from "node:http";
import https from "node:https";

const recorded = JSON.parse(fs.readFileSync(process.env.GITHUB_RECORDING || "/recorded/rate-limit-used-up.json", "utf8")).exchanges[0].reply;
const resetEpoch = Math.floor(Date.now() / 1000) + 3600;
const seen = [];

const log = (msg) => console.log(new Date().toTimeString().slice(0, 8) + " " + msg);

const api = https.createServer({ key: fs.readFileSync("/certs/leaf/api.key"), cert: fs.readFileSync("/certs/leaf/api.pem") }, (req, res) => {
  seen.push(`${req.method} ${req.url}`);
  log(`api.github.com: ${req.method} ${req.url} -> ${recorded.status} (limit used up, resets ${resetEpoch})`);
  const body = JSON.stringify(recorded.body);
  res.writeHead(recorded.status, { ...recorded.headers, "x-ratelimit-reset": String(resetEpoch), "content-length": Buffer.byteLength(body) });
  res.end(req.method === "HEAD" ? undefined : body);
});
api.listen(443, "0.0.0.0");

// What the spec reads: when the stand-in says the limit lifts, and what was asked of it.
http.createServer((req, res) => {
  res.writeHead(200, { "content-type": "application/json" });
  res.end(JSON.stringify({ resetEpoch, calls: seen.length, requests: seen.slice(-50) }));
}).listen(8081, "0.0.0.0");
log(`stand-in for api.github.com on 443, limit resets at ${resetEpoch}`);
