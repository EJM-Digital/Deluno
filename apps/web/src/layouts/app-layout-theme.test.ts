import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * The topbar toggle and the command palette's toggle both called next-themes'
 * setTheme directly, so a click there changed what you saw without ever
 * saving it — meaning it could disagree with whatever Settings -> Interface
 * had saved (#736). Both now go through setThemeAndSave, which applies the
 * theme and PATCHes it to /api/settings in the same call, so the header
 * toggle and the settings page can't drift apart.
 */
describe("header theme toggle saves the choice (#736)", () => {
  const source = readFileSync(resolve(__dirname, "app-layout.tsx"), "utf8");

  it("wraps setTheme in a save so both toggles persist the choice", () => {
    expect(source).toMatch(/const setThemeAndSave = useCallback/);
    expect(source).toMatch(/body: JSON\.stringify\(\{ uiTheme: theme \}\)/);
  });

  it("wires both the command palette and the topbar toggle through it, not the raw setTheme", () => {
    expect(source).toMatch(/onToggleTheme=\{\(\) => setThemeAndSave\(/);
    expect(source).toMatch(/setTheme=\{setThemeAndSave\}/);
  });
});
