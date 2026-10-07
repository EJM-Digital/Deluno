// The download clients the installer puts on are pinned to a version and a checksum (installer/bundled-components.json,
// src/Deluno.Companions/CompanionDownloads.cs, scripts/lab/rig-software.json). A pin left alone goes stale and the
// connection notices then never offer Update now (#1131: all four were behind on 7 Oct 2026, and NZBGet came from a
// repository archived in 2024).
//
//   node scripts/check-component-pins.mjs --offline   the three pin files agree with each other (runs in ci:check)
//   node scripts/check-component-pins.mjs             also asks GitHub for each project's newest release and fails when a
//                                                     pin is a major version, or more than one minor version, behind, or
//                                                     when a project the installer takes from has been archived (run for
//                                                     every Deluno release)
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

export function parseVersion(text) {
  const match = /(\d+)\.(\d+)(?:\.(\d+))?/.exec(String(text ?? ""));
  return match ? { major: Number(match[1]), minor: Number(match[2]), patch: Number(match[3] ?? 0) } : null;
}

/** True when the pin is a major version behind, or more than one minor version behind, the newest release. */
export function isStale(pinned, latest) {
  const pin = parseVersion(pinned);
  const newest = parseVersion(latest);
  if (!pin || !newest) return false;
  if (newest.major !== pin.major) return newest.major > pin.major;
  return newest.minor - pin.minor > 1;
}

/** "owner/repo" out of a GitHub URL, or null. */
export function repositoryOf(url) {
  const match = /github\.com\/([^/]+\/[^/]+)\//.exec(String(url ?? ""));
  return match ? match[1] : null;
}

/** The pinned components in installer/bundled-components.json: a version other than "latest", with the project it comes from. */
export function pinnedComponents(manifest) {
  return Object.entries(manifest)
    .filter(([key, value]) => !key.startsWith("$") && value && typeof value === "object" && value.version && value.version !== "latest")
    .map(([kind, value]) => ({ kind, version: value.version, repository: repositoryOf(value.url), url: value.url, sha256: value.sha256 ?? null, fileName: value.fileName }));
}

/** Pins that disagree between the files, or that name a version their own download link does not carry. */
export function consistencyProblems(manifest, companionSource, rigSoftware) {
  const problems = [];
  for (const pin of pinnedComponents(manifest)) {
    if (pin.url && !pin.url.includes(pin.version)) problems.push(`${pin.kind}: the download link does not carry version ${pin.version}.`);
    if (pin.fileName && !pin.fileName.includes(pin.version)) problems.push(`${pin.kind}: the file name ${pin.fileName} does not carry version ${pin.version}.`);
    const constant = { qbittorrent: "QbittorrentVersion", sabnzbd: "SabnzbdVersion" }[pin.kind];
    if (constant) {
      const code = new RegExp(`${constant}\\s*=\\s*"([^"]+)"`).exec(companionSource)?.[1];
      if (code !== pin.version) problems.push(`${pin.kind}: CompanionDownloads.cs pins ${code ?? "nothing"}, the installer list pins ${pin.version}.`);
      if (pin.sha256 && !companionSource.toLowerCase().includes(pin.sha256.toLowerCase())) {
        problems.push(`${pin.kind}: CompanionDownloads.cs does not pin the checksum the installer list pins.`);
      }
    }
    const rig = rigSoftware?.[pin.kind];
    if (rig && rig.version !== pin.version) problems.push(`${pin.kind}: scripts/lab/rig-software.json pins ${rig.version}, the installer list pins ${pin.version}.`);
    if (rig && pin.sha256 && rig.sha256 && rig.sha256.toLowerCase() !== pin.sha256.toLowerCase()) problems.push(`${pin.kind}: scripts/lab/rig-software.json and the installer list pin different checksums.`);
  }
  return problems;
}

async function latestRelease(repository) {
  const headers = { Accept: "application/vnd.github+json", "User-Agent": "deluno-pin-check" };
  if (process.env.GH_TOKEN || process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GH_TOKEN ?? process.env.GITHUB_TOKEN}`;
  const [release, repo] = await Promise.all([
    fetch(`https://api.github.com/repos/${repository}/releases/latest`, { headers }),
    fetch(`https://api.github.com/repos/${repository}`, { headers })
  ]);
  if (!release.ok || !repo.ok) throw new Error(`GitHub answered ${release.status}/${repo.status} for ${repository}.`);
  return { tag: (await release.json()).tag_name, archived: (await repo.json()).archived === true };
}

async function main() {
  const manifest = JSON.parse(readFileSync(join(root, "installer/bundled-components.json"), "utf8"));
  const companionSource = readFileSync(join(root, "src/Deluno.Companions/CompanionDownloads.cs"), "utf8");
  const rigPath = join(root, "scripts/lab/rig-software.json");
  const rig = existsSync(rigPath) ? JSON.parse(readFileSync(rigPath, "utf8")) : null;

  const problems = consistencyProblems(manifest, companionSource, rig);
  if (!process.argv.includes("--offline")) {
    const repositories = new Map();
    for (const pin of pinnedComponents(manifest)) if (pin.repository) repositories.set(pin.repository, pin);
    for (const [kind, value] of Object.entries(manifest)) {
      if (!kind.startsWith("$") && value?.githubRepo && !repositories.has(value.githubRepo)) repositories.set(value.githubRepo, { kind, version: null });
    }

    for (const [repository, pin] of repositories) {
      try {
        const latest = await latestRelease(repository);
        if (latest.archived) problems.push(`${pin.kind}: ${repository} is archived. Take it from the project that replaced it.`);
        if (pin.version && isStale(pin.version, latest.tag)) problems.push(`${pin.kind}: pinned ${pin.version}, but ${repository} is at ${latest.tag}. Bump the pin with the project's published checksum.`);
        else console.log(`${pin.kind}: ${pin.version ?? "latest release"} against ${latest.tag} is fine.`);
      } catch (error) {
        console.warn(`${pin.kind}: could not ask GitHub (${error.message}); not judged.`);
      }
    }
  }

  if (problems.length > 0) {
    for (const problem of problems) console.error(problem);
    process.exit(1);
  }
  console.log("The installer's pinned components agree and are not stale.");
}

if (process.argv[1] && import.meta.url === new URL(`file:///${process.argv[1].replace(/\\/g, "/").replace(/^\//, "")}`).href) {
  await main();
}
