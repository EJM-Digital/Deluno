/**
 * #936: every automatic behaviour that reads off gets a plain-words notice
 * here, grouped into one compact card rather than one banner per behaviour.
 * "Keep off" has to stick, so the dismissal is a real setting
 * (`dismissedAutomationNotices`) saved through the same settings API the
 * setup guide uses — never `localStorage`, which only that one browser
 * would ever see.
 */

import { useState } from "react";
import { Link } from "react-router-dom";
import { PowerOff } from "lucide-react";
import type { LibraryItem, PlatformSettingsSnapshot } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { Button } from "../ui/button";

export type AutomationNoticeKey =
  | "searching"
  | "renameOnImport"
  | "subtitleFetching"
  | "removeBadDownloads"
  | "deleteBadDownloadFiles"
  | "leftoverCleanup";

export interface AutomationNotice {
  key: AutomationNoticeKey;
  text: string;
  /**
   * The settings PATCH body that turns this one behaviour on, when a single
   * settings-page flag covers it. Subtitle fetching has no such flag yet
   * (it is chosen per library, per language), so it carries `turnOnHref`
   * instead, pointing at where it is actually configured.
   */
  turnOnPatch?: Record<string, boolean>;
  turnOnHref?: string;
}

function parseDismissed(value: string): Set<AutomationNoticeKey> {
  return new Set(
    value
      .split(",")
      .map((item) => item.trim())
      .filter((item): item is AutomationNoticeKey => item.length > 0)
  );
}

function serializeDismissed(keys: Iterable<AutomationNoticeKey>): string {
  return [...new Set(keys)].join(",");
}

/**
 * Every automatic behaviour that currently reads off, in the plain words
 * #936 asks for. Subtitle fetching has no single settings-page flag yet, so
 * it is read from whether any library has subtitle languages configured —
 * an install with no library at all is not shown that notice, since there
 * is nothing yet for it to describe.
 */
export function computeOffAutomationNotices(
  settings: PlatformSettingsSnapshot,
  libraries: LibraryItem[]
): AutomationNotice[] {
  const notices: AutomationNotice[] = [];

  if (!settings.autoStartJobs) {
    notices.push({
      key: "searching",
      text: "Automatic searching is off, so nothing will download until you turn it on.",
      turnOnPatch: { autoStartJobs: true }
    });
  }

  if (!settings.renameOnImport) {
    notices.push({
      key: "renameOnImport",
      text: "Renaming files on import is off, so files keep the name they arrived with.",
      turnOnPatch: { renameOnImport: true }
    });
  }

  if (libraries.length > 0 && libraries.every((library) => !library.subtitleLanguages?.length)) {
    notices.push({
      key: "subtitleFetching",
      text: "Automatic subtitle fetching is off, so you add subtitles yourself.",
      turnOnHref: "/settings"
    });
  }

  if (!settings.cleanupRemoveClientEntryAfterThreshold) {
    notices.push({
      key: "removeBadDownloads",
      text: "Removing a bad download from the client is off, so you remove it yourself.",
      turnOnPatch: { cleanupRemoveClientEntryAfterThreshold: true }
    });
  }

  if (!settings.cleanupPurgePayloadAfterThreshold) {
    notices.push({
      key: "deleteBadDownloadFiles",
      text: "Deleting a bad download's files is off, so its files stay until you remove them.",
      turnOnPatch: { cleanupPurgePayloadAfterThreshold: true }
    });
  }

  if (!settings.cleanupEmptyFolders) {
    notices.push({
      key: "leftoverCleanup",
      text: "Cleaning up empty leftover folders is off, so they stay until you remove them.",
      turnOnPatch: { cleanupEmptyFolders: true }
    });
  }

  return notices;
}

/** The notices #936 wants shown: off, and not already dismissed with "Keep off". */
export function computeVisibleAutomationNotices(
  settings: PlatformSettingsSnapshot,
  libraries: LibraryItem[]
): AutomationNotice[] {
  const dismissed = parseDismissed(settings.dismissedAutomationNotices);
  return computeOffAutomationNotices(settings, libraries).filter((notice) => !dismissed.has(notice.key));
}

export function AutomationNoticeCard({
  settings,
  libraries
}: {
  settings: PlatformSettingsSnapshot;
  libraries: LibraryItem[];
}) {
  const [pendingKey, setPendingKey] = useState<AutomationNoticeKey | null>(null);
  const notices = computeVisibleAutomationNotices(settings, libraries);

  if (notices.length === 0) return null;

  async function patchSettings(body: Record<string, unknown>) {
    await authedFetch("/api/settings", {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
  }

  async function turnOn(notice: AutomationNotice) {
    if (!notice.turnOnPatch) return;
    setPendingKey(notice.key);
    try {
      await patchSettings(notice.turnOnPatch);
    } finally {
      setPendingKey(null);
    }
  }

  async function keepOff(notice: AutomationNotice) {
    setPendingKey(notice.key);
    const dismissed = parseDismissed(settings.dismissedAutomationNotices);
    dismissed.add(notice.key);
    try {
      await patchSettings({ dismissedAutomationNotices: serializeDismissed(dismissed) });
    } finally {
      setPendingKey(null);
    }
  }

  return (
    <section aria-label="Off by itself" className="rounded-2xl border border-hairline bg-card p-4">
      <div className="mb-3 flex items-center gap-2">
        <PowerOff className="h-4 w-4 text-muted-foreground" />
        <h2 className="font-display text-base font-semibold tracking-tight text-foreground">Off by itself</h2>
      </div>
      <div className="divide-y divide-hairline">
        {notices.map((notice) => (
          <div key={notice.key} className="flex flex-col gap-2 py-3 first:pt-0 last:pb-0 sm:flex-row sm:items-center sm:justify-between">
            <p className="text-[length:var(--type-body-sm)] text-muted-foreground">{notice.text}</p>
            <div className="flex shrink-0 gap-2">
              {notice.turnOnHref ? (
                <Button type="button" size="sm" variant="outline" asChild>
                  <Link to={notice.turnOnHref}>Turn on</Link>
                </Button>
              ) : (
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={pendingKey === notice.key}
                  onClick={() => void turnOn(notice)}
                >
                  Turn on
                </Button>
              )}
              <Button
                type="button"
                size="sm"
                variant="ghost"
                disabled={pendingKey === notice.key}
                onClick={() => void keepOff(notice)}
              >
                Keep off
              </Button>
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}
