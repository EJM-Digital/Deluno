import { describe, expect, it } from "vitest";
import {
  ALERT_LABEL_MAX,
  connectionDownActions,
  failedImportActions,
  handoffActions,
  healthProblemActions,
  noReleaseActions,
  recoveryActions,
  stuckDownloadActions,
  updateReadyActions,
  type AlertActions
} from "./alert-actions";

const queueItem = (over: Record<string, unknown> = {}) =>
  ({ id: "t1", clientId: "qb", clientName: "qBittorrent", title: "Sintel", releaseName: "Sintel.1080p", status: "stalled", errorMessage: null, healthFindings: null, ...over }) as never;

function labels(actions: AlertActions) {
  return actions.map((action) => action.label);
}

describe("alert buttons (#1098)", () => {
  it("every problem has one or two buttons, short labels, hover notes, and either a request or a link", () => {
    const all: AlertActions[] = [
      stuckDownloadActions(queueItem()),
      stuckDownloadActions(queueItem({ healthFindings: [{ kind: "stalled" }] })),
      recoveryActions({ id: "c1", mediaType: "movie", title: "Sintel", failureKind: "importFailed", detailsJson: JSON.stringify({ DispatchId: "d1" }) } as never),
      recoveryActions({ id: "c2", mediaType: "series", title: "Show", failureKind: "vanished-downloading", detailsJson: JSON.stringify({ DispatchId: "d1" }) } as never),
      recoveryActions({ id: "c3", mediaType: "movie", title: "Sintel", failureKind: "processedFileIsLess", detailsJson: null } as never),
      failedImportActions({ id: "j1" }),
      handoffActions({ id: "h1", releaseName: "R" }),
      noReleaseActions({ mediaType: "movie", id: "m1", title: "Film", year: null, libraryId: "lib", lastSearchUtc: "", missingSinceUtc: null, reason: null }),
      connectionDownActions("indexer", "ix1", "NZBgeek"),
      connectionDownActions("downloadClient", "c1", "qBittorrent"),
      updateReadyActions(),
      ...["machine:sleep", "machine:drive:WD Red", "machine:reboot", "machine:temperature", "system:disk-space", "download-client:c1:free-space", "indexer:ix1", "download-client:c1", "processor:p1", "media-server:ms1", "seerr:s1", "library:l1:root", "system:clock", "tags:unused"].map((id) => healthProblemActions({ id, link: "/system" }))
    ];

    for (const actions of all) {
      expect(actions.length === 1 || actions.length === 2).toBe(true);
      const ids = actions.map((action) => action.id);
      expect(new Set(ids).size).toBe(ids.length);
      for (const action of actions) {
        expect(action.label.length).toBeLessThanOrEqual(ALERT_LABEL_MAX);
        expect(action.hint.length).toBeGreaterThan(15);
        expect(Boolean(action.request) !== Boolean(action.to)).toBe(true);
        expect(action.label).not.toMatch(/give up/i);
      }
    }
  });

  it("Dismiss says it stops showing the problem and deletes nothing", () => {
    const dismissals = [
      failedImportActions({ id: "j1" })[1]!,
      handoffActions({ id: "h1", releaseName: "R" })[1]!,
      recoveryActions({ id: "c1", mediaType: "movie", title: "Sintel", failureKind: "importFailed", detailsJson: JSON.stringify({ DispatchId: "d1" }) } as never)[1]!
    ];
    for (const action of dismissals) {
      expect(action.label).toBe("Dismiss");
      expect(action.hint).toBe("Stop showing this. Nothing is deleted.");
      expect(action.quiet).toBe(true);
    }
  });

  it("a stuck download offers the stronger fix by name, then Dismiss when the client has a finding to silence, else Open Transfers", () => {
    const plain = stuckDownloadActions(queueItem());
    expect(labels(plain)).toEqual(["Find another", "Open Transfers"]);
    expect(plain[0].request).toMatchObject({ method: "POST", path: "/api/download-clients/qb/queue/t1/remove", body: { reason: "failed" } });
    expect(plain[0].hint).toMatch(/Removes it from qBittorrent, deletes its files, blocklists this release/);

    const withFinding = stuckDownloadActions(queueItem({ healthFindings: [{ kind: "stalled" }] }));
    expect(labels(withFinding)).toEqual(["Find another", "Dismiss"]);
    expect(withFinding[1]!.request).toMatchObject({ path: "/api/download-clients/qb/queue/t1/health/stalled/ignore", body: { durationDays: 7 } });
  });

  it("a download the client no longer lists is searched for again, and an import that can be retried is retried", () => {
    const vanished = recoveryActions({ id: "c2", mediaType: "series", title: "Show", failureKind: "vanished-downloading", detailsJson: JSON.stringify({ DispatchId: "d1" }) } as never);
    expect(labels(vanished)).toEqual(["Search now", "Dismiss"]);
    expect(vanished[0].request?.path).toBe("/api/series/import-recovery/c2/retry");

    const retryable = recoveryActions({ id: "c1", mediaType: "movie", title: "Sintel", failureKind: "importFailed", detailsJson: JSON.stringify({ DispatchId: "d1" }) } as never);
    expect(labels(retryable)).toEqual(["Retry", "Dismiss"]);
  });

  it("the Machine problems have buttons that say what they do", () => {
    expect(labels(healthProblemActions({ id: "machine:sleep" }))).toEqual(["Keep awake", "Snooze 1 day"]);
    const keepAwake = healthProblemActions({ id: "machine:sleep" })[0];
    expect(keepAwake.request).toMatchObject({ method: "PATCH", path: "/api/settings", body: { keepComputerAwake: true } });
    expect(keepAwake.hint).toMatch(/power settings are not changed/);

    const drive = healthProblemActions({ id: "machine:drive:WD Red" });
    expect(labels(drive)).toEqual(["Back up now", "Snooze 1 day"]);
    expect(drive[0].request).toMatchObject({ method: "POST", path: "/api/backups" });

    expect(labels(healthProblemActions({ id: "machine:reboot" }))).toEqual(["Snooze 1 day", "Snooze 1 week"]);
    expect(labels(healthProblemActions({ id: "machine:temperature" }))).toEqual(["Snooze 1 day", "Snooze 1 week"]);
    expect(healthProblemActions({ id: "machine:reboot" })[1]!.request?.body).toEqual({ hours: 168 });
  });

  it("a connection problem tests that connection again and, for an indexer or client, can switch it off", () => {
    const indexer = healthProblemActions({ id: "indexer:ix1" });
    expect(labels(indexer)).toEqual(["Test again", "Switch off"]);
    expect(indexer[0].request).toMatchObject({ method: "POST", path: "/api/connections/indexer/ix1/test" });
    expect(indexer[1]!.request).toMatchObject({ method: "PUT", path: "/api/indexers/ix1", body: { isEnabled: false } });

    const client = healthProblemActions({ id: "download-client:c1" });
    expect(client[0].request?.path).toBe("/api/connections/downloadClient/c1/test");
    expect(client[1]!.request?.path).toBe("/api/download-clients/c1");

    expect(healthProblemActions({ id: "processor:p1" })[0].request?.path).toBe("/api/connections/processor/p1/test");
    expect(healthProblemActions({ id: "seerr:s1" })[0].request?.path).toBe("/api/connections/requests/s1/test");
    // A free-space finding about a client is not a connection to test.
    expect(labels(healthProblemActions({ id: "download-client:c1:free-space" }))).toEqual(["Clean up", "Snooze 1 day"]);
  });

  it("any other health problem goes to the screen that clears it, or can be snoozed for a day", () => {
    expect(labels(healthProblemActions({ id: "tags:unused", link: "/settings/tags" }))).toEqual(["Open Settings", "Snooze 1 day"]);
    expect(labels(healthProblemActions({ id: "system:clock", link: "/system" }))).toEqual(["Open System", "Snooze 1 day"]);
    const snooze = healthProblemActions({ id: "system:clock" })[1]!;
    expect(snooze.request).toMatchObject({ method: "POST", path: "/api/system/health/system%3Aclock/snooze", body: { hours: 24 } });
    expect(snooze.hint).toMatch(/not fixed or changed/);
  });
});
