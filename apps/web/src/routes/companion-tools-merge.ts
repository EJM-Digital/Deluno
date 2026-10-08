import type { BundledComponentStatus } from "../lib/api";

/** One row on the Companion Tools page. Mirrors `Deluno.Companions.CompanionToolStatus`. */
export interface CompanionTool {
  id: string;
  name: string;
  description: string;
  state: "not-installed" | "installed" | "connected" | string;
  connectionName: string | null;
  /** Where the tool answers on this computer, when Deluno knows it. */
  url?: string | null;
  job: { status: "queued" | "running" | "done" | "skipped" | "failed" | string; step: string; message: string; updatedUtc: string } | null;
  /** An app Deluno's installer put on this computer that this page cannot set up again; it is listed so the count covers it. */
  installedByInstaller?: boolean;
  /** An app only the tray's installer can set up: Install asks the tray icon to open its installer window for it. */
  installedByTray?: boolean;
}

export interface CompanionToolsResponse {
  availability: { available: boolean; reason: string | null };
  tools: CompanionTool[];
  uninstallNote: string;
}

/**
 * The page's own tools (SABnzbd, qBittorrent, Weir) plus every other app the installer can put on this computer
 * (Transmission, Deluge, NZBGet), installed or not: the page lists every app the picker can install, so an app the
 * picker could not install (GitHub's limit stopped it) is still here with Install, and "x of y connected" counts
 * them all.
 */
export function withInstalledApps(response: CompanionToolsResponse, installed: readonly BundledComponentStatus[]): CompanionToolsResponse {
  const known = new Set(response.tools.map((tool) => tool.id.toLowerCase()));
  const extra: CompanionTool[] = installed
    .filter((app) => !known.has(app.kind.toLowerCase()))
    .map((app) => ({
      id: app.kind,
      name: app.displayName,
      description: app.installed || app.connected ? "Installed with Deluno." : "Deluno's installer can put this on this computer.",
      state: app.connected ? "connected" : app.installed ? "installed" : "not-installed",
      connectionName: app.connected ? app.displayName : null,
      url: null,
      job: null,
      installedByInstaller: app.installed || app.connected,
      installedByTray: !(app.installed || app.connected)
    }));
  return extra.length === 0 ? response : { ...response, tools: [...response.tools, ...extra] };
}
