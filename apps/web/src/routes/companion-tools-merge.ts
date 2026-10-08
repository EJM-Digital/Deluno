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
}

export interface CompanionToolsResponse {
  availability: { available: boolean; reason: string | null };
  tools: CompanionTool[];
  uninstallNote: string;
}

/**
 * The page's own tools (SABnzbd, qBittorrent, Weir) plus every other app the installer put on this computer
 * (Transmission, Deluge, NZBGet), so "x of y connected" counts every installed companion, not just three.
 * An app that is not installed and not connected is not added.
 */
export function withInstalledApps(response: CompanionToolsResponse, installed: readonly BundledComponentStatus[]): CompanionToolsResponse {
  const known = new Set(response.tools.map((tool) => tool.id.toLowerCase()));
  const extra: CompanionTool[] = installed
    .filter((app) => !known.has(app.kind.toLowerCase()) && (app.installed || app.connected))
    .map((app) => ({
      id: app.kind,
      name: app.displayName,
      description: "Installed with Deluno.",
      state: app.connected ? "connected" : "installed",
      connectionName: app.connected ? app.displayName : null,
      url: null,
      job: null,
      installedByInstaller: true
    }));
  return extra.length === 0 ? response : { ...response, tools: [...response.tools, ...extra] };
}
