import { describe, expect, it } from "vitest";
import { getGlobalShortcuts } from "./command-registry";
import { commandPaletteShortcut, isApplePlatform } from "./platform-shortcuts";

describe("platform shortcuts", () => {
  it("recognises Apple desktop and mobile platforms", () => {
    expect(isApplePlatform("MacIntel")).toBe(true);
    expect(isApplePlatform("iPhone")).toBe(true);
    expect(isApplePlatform("Win32")).toBe(false);
  });

  it("uses Command on Apple platforms", () => {
    expect(commandPaletteShortcut("MacIntel")).toEqual({ label: "⌘ K", ariaKeyshortcuts: "Meta+K" });
  });

  it("uses Ctrl everywhere else", () => {
    expect(commandPaletteShortcut("Win32")).toEqual({ label: "Ctrl K", ariaKeyshortcuts: "Control+K" });
    expect(commandPaletteShortcut("Linux x86_64")).toEqual({ label: "Ctrl K", ariaKeyshortcuts: "Control+K" });
  });

  it("keeps the keyboard help overlay aligned with the button label", () => {
    expect(getGlobalShortcuts("MacIntel")[0]).toMatchObject({ keys: ["⌘", "K"], label: "Open search & navigate" });
    expect(getGlobalShortcuts("Win32")[0]).toMatchObject({ keys: ["Ctrl", "K"], label: "Open search & navigate" });
  });
});
