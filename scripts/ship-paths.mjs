#!/usr/bin/env node
// The one rule for which files "ship": a change to any of them makes a different product, so a golden-path pass
// on an earlier commit no longer counts (James, 16 Oct 2026). Everything else (tests, scripts that only check or
// run things, docs, CI files) cannot change what is installed, so a pass carries over it.
//
// Derived from what the Windows installer and the Docker image read, not guessed:
//   Dockerfile COPY lines   package.json, package-lock.json, apps/web, sdk/typescript/package.json, NuGet.Config,
//                           global.json, Directory.Build.props, RELEASE_NOTES.md (embedded by Deluno.Api), src
//   Deluno.Tray.csproj      installer/deluno.ico and installer/bundled-components.json (embedded), apps/windows-tray,
//                           and the web build output; it references src/** projects
//   ci.yml windows-installer, scripts/pack-windows-installer.ps1
//                           npm run build:web, dotnet publish of the tray, scripts/fetch-ffmpeg.ps1 and
//                           scripts/fetch-unrar.ps1 (what is bundled), the pinned vpk tool (.config), installer/
// It errs on the side of "ships": a path is exempt only when it is listed below as something the build never reads.
// A change to Directory.Build.props is always a shipped change, so a different <Version> is never carried over.
//
// Usage:  node scripts/ship-paths.mjs --base <sha> --head <sha>    (exit 0 when nothing that ships differs)
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

// Paths that ship, as prefixes (ending in "/") or exact files.
export const SHIPPED_PREFIXES = [
  "src/",
  "apps/web/",
  "apps/windows-tray/",
  "sdk/",
  "services/",
  "integrations/",
  "installer/",
  ".config/",
];

export const SHIPPED_FILES = [
  "Directory.Build.props",
  "RELEASE_NOTES.md",
  "Dockerfile",
  ".dockerignore",
  "compose.yaml",
  "package.json",
  "package-lock.json",
  "NuGet.Config",
  "global.json",
  "Deluno.slnx",
  "scripts/pack-windows-installer.ps1",
  "scripts/publish-windows.ps1",
  "scripts/fetch-ffmpeg.ps1",
  "scripts/fetch-unrar.ps1",
];

// Inside a shipped folder, the files the build never reads: tests, and checks that only run beside the build.
const EXEMPT_PATTERNS = [
  /^apps\/web\/tests\//,
  /^apps\/web\/scripts\//,
  /^apps\/web\/(playwright|stryker|knip)[^/]*\.(ts|js|json)$/,
  /^apps\/web\/[^/]+\.md$/,
  /\.(test|spec)\.(ts|tsx|js|jsx|mjs|cjs)$/,
  /^sdk\/[^/]+\/tests?\//,
  /^services\/[^/]+\/tests?\//,
];

export function isShippedPath(path) {
  const file = String(path).replace(/\\/g, "/").replace(/^\.\//, "");
  const inside = SHIPPED_FILES.includes(file) || SHIPPED_PREFIXES.some((prefix) => file.startsWith(prefix));
  return inside && !EXEMPT_PATTERNS.some((pattern) => pattern.test(file));
}

export function shippedPaths(paths) {
  return paths.filter(isShippedPath);
}

const defaultGit = (args) => execFileSync("git", args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });

export function isAncestor(base, head, git = defaultGit) {
  try {
    git(["merge-base", "--is-ancestor", base, head]);
    return true;
  } catch (error) {
    // Exit 1 means "not an ancestor"; anything else (a commit not fetched, a bad name) is not a yes either.
    return false;
  }
}

// Every path that differs between the two commits' trees (a rename counts as both paths).
export function changedPaths(base, head, git = defaultGit) {
  return git(["diff", "--name-only", "--no-renames", "-z", base, head]).split("\0").filter(Boolean);
}

// Whether a golden-path pass on `base` counts for `head`. Returns { ok, reason, shipped }.
export function sameShippedFiles(base, head, git = defaultGit) {
  if (base === head) return { ok: true, reason: "the same commit", shipped: [] };
  if (!isAncestor(base, head, git)) return { ok: false, reason: `${base} is not an ancestor of ${head}`, shipped: [] };
  const shipped = shippedPaths(changedPaths(base, head, git));
  return shipped.length === 0
    ? { ok: true, reason: "no file that ships changed", shipped }
    : { ok: false, reason: `${shipped.length} file(s) that ship changed: ${shipped.slice(0, 5).join(", ")}${shipped.length > 5 ? ", ..." : ""}`, shipped };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const argv = process.argv.slice(2);
  const option = (flag) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : undefined);
  const result = sameShippedFiles(option("--base"), option("--head"));
  console.log(result.ok ? `Same shipped files: ${result.reason}.` : `Different: ${result.reason}.`);
  process.exit(result.ok ? 0 : 1);
}
