import { describe, expect, it, vi } from "vitest";
import {
  STALE_BUILD_RELOAD_WINDOW_MS,
  installStaleBuildRecovery,
  isStaleBuildError,
  reloadOntoNewBuild
} from "./stale-build";

function tab(stored: Record<string, string> = {}) {
  const store = { ...stored };
  return {
    store,
    reload: vi.fn(),
    assign: vi.fn(),
    get target() {
      return {
        sessionStorage: {
          getItem: (key: string) => store[key] ?? null,
          setItem: (key: string, value: string) => { store[key] = value; }
        },
        location: { reload: this.reload, assign: this.assign }
      };
    }
  };
}

describe("a tab running an older build (#622)", () => {
  it("recognises each browser's words for a screen the update replaced", () => {
    expect(isStaleBuildError(new TypeError("Failed to fetch dynamically imported module: http://192.168.1.51:5099/assets/connections-screen.DoED8niD.js"))).toBe(true);
    expect(isStaleBuildError(new TypeError("error loading dynamically imported module"))).toBe(true);
    expect(isStaleBuildError(new TypeError("Importing a module script failed."))).toBe(true);
    expect(isStaleBuildError(new Error("Unable to preload CSS for /assets/index.css"))).toBe(true);
  });

  it("leaves every other failure alone", () => {
    expect(isStaleBuildError(new Error("The route loader failed."))).toBe(false);
    expect(isStaleBuildError(undefined)).toBe(false);
    expect(isStaleBuildError({ status: 500 })).toBe(false);
  });

  it("reloads onto the new build once", () => {
    const current = tab();
    expect(reloadOntoNewBuild(current.target, 1_000_000)).toBe(true);
    expect(current.reload).toHaveBeenCalledTimes(1);
  });

  it("does not reload again when the last reload did not help", () => {
    const current = tab();
    reloadOntoNewBuild(current.target, 1_000_000);

    expect(reloadOntoNewBuild(current.target, 1_000_000 + 5_000)).toBe(false);
    expect(current.reload).toHaveBeenCalledTimes(1);
  });

  it("reloads again for a later update", () => {
    const current = tab();
    reloadOntoNewBuild(current.target, 1_000_000);

    expect(reloadOntoNewBuild(current.target, 1_000_000 + STALE_BUILD_RELOAD_WINDOW_MS + 1)).toBe(true);
    expect(current.reload).toHaveBeenCalledTimes(2);
  });

  it("does not reload without somewhere to remember it did", () => {
    const reload = vi.fn();
    const target = {
      sessionStorage: {
        getItem: () => { throw new Error("blocked"); },
        setItem: () => { throw new Error("blocked"); }
      },
      location: { reload, assign: vi.fn() }
    };

    expect(reloadOntoNewBuild(target)).toBe(false);
    expect(reload).not.toHaveBeenCalled();
  });

  it("loads the page being opened, not the one the click came from", () => {
    const current = tab();
    const target = Object.assign(new EventTarget(), current.target);
    installStaleBuildRecovery(target, () => "/settings/libraries");

    target.dispatchEvent(new Event("vite:preloadError", { cancelable: true }));

    expect(current.assign).toHaveBeenCalledWith("/settings/libraries");
    expect(current.reload).not.toHaveBeenCalled();
  });

  it("reloads when Vite reports a screen's file failed, before the error screen shows", () => {
    const current = tab();
    const target = Object.assign(new EventTarget(), current.target);
    installStaleBuildRecovery(target);

    const event = new Event("vite:preloadError", { cancelable: true });
    target.dispatchEvent(event);

    expect(current.reload).toHaveBeenCalledTimes(1);
    expect(event.defaultPrevented).toBe(true);
  });
});
