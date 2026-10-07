import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { consistencyProblems, isStale, pinnedComponents, repositoryOf } from "./check-component-pins.mjs";

test("a pin is stale when a major version behind or more than one minor version behind", () => {
  assert.equal(isStale("5.0.4", "5.1.3"), false);
  assert.equal(isStale("5.0.4", "5.2.0"), true);
  assert.equal(isStale("5.2.1", "5.2.4"), false);
  assert.equal(isStale("4.9.0", "5.0.0"), true);
  assert.equal(isStale("5.2.4", "release-5.2.4"), false);
  assert.equal(isStale("21.1", "v26.3"), true);
  assert.equal(isStale("5.1.3", "not a version"), false);
});

test("the repository comes out of a GitHub download link", () => {
  assert.equal(repositoryOf("https://github.com/sabnzbd/sabnzbd/releases/download/5.1.3/x.exe"), "sabnzbd/sabnzbd");
  assert.equal(repositoryOf("https://aka.ms/vs/17/release/vc_redist.x64.exe"), null);
});

test("the repository's own pin files agree", () => {
  const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
  const manifest = JSON.parse(read("installer/bundled-components.json"));
  const rig = JSON.parse(read("scripts/lab/rig-software.json"));
  assert.deepEqual(consistencyProblems(manifest, read("src/Deluno.Companions/CompanionDownloads.cs"), rig), []);
  assert.ok(pinnedComponents(manifest).length >= 3);
});

test("a pin that differs between files is named", () => {
  const manifest = { qbittorrent: { version: "5.2.4", url: "https://github.com/qbittorrent/qBittorrent/releases/download/release-5.2.4/q_5.2.4.exe", fileName: "q_5.2.4.exe" } };
  const problems = consistencyProblems(manifest, 'QbittorrentVersion = "5.2.1";', { qbittorrent: { version: "5.2.1" } });
  assert.equal(problems.length, 2);
});

test("NZBGet is taken from the maintained project, never the archived one", () => {
  const manifest = JSON.parse(readFileSync(new URL("../installer/bundled-components.json", import.meta.url), "utf8"));
  assert.equal(manifest.nzbget.githubRepo, "nzbgetcom/nzbget");
  assert.match("nzbget-26.3-bin-windows-setup.exe", new RegExp(manifest.nzbget.githubAssetPattern));
  assert.doesNotMatch("nzbget-26.3-bin-windows-debug-setup.exe", new RegExp(manifest.nzbget.githubAssetPattern));
  assert.match("nzbget-26.3.sig.txt", new RegExp(manifest.nzbget.githubChecksumAssetPattern));
});
