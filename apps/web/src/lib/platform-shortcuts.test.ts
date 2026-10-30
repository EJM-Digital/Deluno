import { describe, expect, it, vi } from "vitest";
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

describe("platform shortcuts read from the browser", () => {
  it("reads the platform from the browser when none is given", () => {
    const spy = vi.spyOn(window.navigator, "platform", "get").mockReturnValue("MacIntel");
    expect(commandPaletteShortcut().label).toBe("⌘ K");
    spy.mockRestore();
  });

  it("uses Ctrl when there is no navigator at all", () => {
    vi.stubGlobal("navigator", undefined);
    expect(commandPaletteShortcut().label).toBe("Ctrl K");
    vi.unstubAllGlobals();
  });
});
