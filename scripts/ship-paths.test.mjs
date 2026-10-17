// node --test scripts/ship-paths.test.mjs
// The one rule for which files ship (scripts/ship-paths.mjs).
import assert from "node:assert/strict";
import { test } from "node:test";

import { isShippedPath, shippedPaths } from "./ship-paths.mjs";

test("everything the installer and the image build from ships", () => {
  for (const path of [
    "src/Deluno.Host/Program.cs", "src/Deluno.Platform/Migrations/V0001.cs", "apps/web/src/main.tsx", "apps/web/index.html",
    "apps/web/package.json", "apps/web/vite.config.ts", "apps/windows-tray/Program.cs", "sdk/typescript/src/index.ts",
    "services/metadata-gateway/src/index.js", "installer/bundled-components.json", "installer/deluno.ico",
    "Directory.Build.props", "RELEASE_NOTES.md", "Dockerfile", "package.json", "package-lock.json", "NuGet.Config",
    "global.json", ".config/dotnet-tools.json", "scripts/fetch-ffmpeg.ps1", "scripts/pack-windows-installer.ps1",
  ]) assert.equal(isShippedPath(path), true, path);
});

test("tests, docs, CI files and checks do not ship", () => {
  for (const path of [
    "tests/Deluno.Movies.Tests/A.cs", "docs/golden-path.md", "AGENTS.md", "README.md", ".github/workflows/ci.yml",
    "scripts/golden-path/Invoke-ScenarioSuite.ps1", "scripts/verify-golden-path-for-release.mjs", "scripts/gate.ps1",
    "apps/web/src/components/a.test.tsx", "apps/web/src/lib/b.spec.ts", "apps/web/tests/e2e/smoke.spec.ts",
    "apps/web/playwright.config.ts", "apps/web/scripts/validate-copy.ts", "sdk/typescript/tests/delunoclient.test.mjs",
    "services/metadata-gateway/test/index.test.js", "screenshots/a.png",
  ]) assert.equal(isShippedPath(path), false, path);
});

test("Windows separators and a leading ./ are read the same", () => {
  assert.equal(isShippedPath("src\\Deluno.Host\\Program.cs"), true);
  assert.equal(isShippedPath("./Dockerfile"), true);
  assert.deepEqual(shippedPaths(["docs/a.md", "src/a.cs"]), ["src/a.cs"]);
});
