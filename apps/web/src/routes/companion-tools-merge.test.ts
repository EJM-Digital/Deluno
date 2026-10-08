import { describe, expect, it } from "vitest";
import type { BundledComponentStatus } from "../lib/api";
import { withInstalledApps, type CompanionTool, type CompanionToolsResponse } from "./companion-tools-merge";

const tool = (id: string, state: string): CompanionTool => ({ id, name: id, description: "", state, connectionName: state === "connected" ? id : null, job: null });
const app = (kind: string, installed: boolean, connected = false): BundledComponentStatus => ({
  kind, displayName: kind, category: "download-client", installed, reachable: installed, defaultHost: "localhost", defaultPort: 1, connected
});
const page = (tools: CompanionTool[]): CompanionToolsResponse => ({ availability: { available: true, reason: null }, tools, uninstallNote: "" });

describe("the Companion Tools count covers every installed app", () => {
  it("adds the installed apps the page cannot set up, so six installed apps count as six", () => {
    const merged = withInstalledApps(
      page([tool("sabnzbd", "connected"), tool("qbittorrent", "connected"), tool("weir", "connected")]),
      [app("qbittorrent", true, true), app("transmission", true), app("deluge", true, true), app("sabnzbd", true, true), app("nzbget", true), app("weir", true, true)]
    );

    expect(merged.tools.map((item) => item.id)).toEqual(["sabnzbd", "qbittorrent", "weir", "transmission", "deluge", "nzbget"]);
    expect(merged.tools.filter((item) => item.state === "connected")).toHaveLength(4);
    expect(merged.tools.find((item) => item.id === "transmission")).toMatchObject({ state: "installed", installedByInstaller: true });
  });

  it("lists an app that is not on this computer with Install, and does not repeat one the page already has", () => {
    const merged = withInstalledApps(page([tool("sabnzbd", "installed")]), [app("sabnzbd", true), app("deluge", false)]);

    expect(merged.tools.map((item) => item.id)).toEqual(["sabnzbd", "deluge"]);
    expect(merged.tools.find((item) => item.id === "deluge")).toMatchObject({ state: "not-installed", installedByTray: true, installedByInstaller: false });
  });

  it("lists NZBGet before it is installed, as GitHub's limit can leave it uninstalled", () => {
    const merged = withInstalledApps(
      page([tool("sabnzbd", "connected"), tool("qbittorrent", "connected"), tool("weir", "installed")]),
      [app("qbittorrent", true, true), app("transmission", true, true), app("deluge", true, true), app("sabnzbd", true, true), app("nzbget", false), app("weir", false)]
    );

    expect(merged.tools.map((item) => item.id)).toEqual(["sabnzbd", "qbittorrent", "weir", "transmission", "deluge", "nzbget"]);
    expect(merged.tools.find((item) => item.id === "nzbget")).toMatchObject({ state: "not-installed", installedByTray: true });
  });
});
