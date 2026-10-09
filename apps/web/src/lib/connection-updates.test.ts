import { describe, expect, it } from "vitest";
import { ALERT_LABEL_MAX } from "./alert-actions";
import { serverRow, summaryOf } from "./attention-summary-fixtures";
import { bellRows } from "./bell-rows";
import { connectionLink, updateNoticeTitle, type ConnectionUpdate } from "./connection-updates";

const qbittorrent: ConnectionUpdate = {
  kind: "downloadClient",
  id: "qb1",
  name: "qBittorrent",
  project: "qbittorrent",
  appName: "qBittorrent",
  version: "4.6.7",
  latestVersion: "5.0.2",
  releaseUrl: "https://github.com/qbittorrent/qBittorrent/releases/tag/release-5.0.2",
  dismissedVersion: null,
  notice: true,
  mode: "manual",
  targetVersion: null,
  message: "Update it where it runs."
};

/** The row the server builds for a notice (`AttentionSummaryBuilder`), which the bell lists in its own group. */
const noticeRow = (entry: ConnectionUpdate) =>
  serverRow({
    id: `connection-update:${entry.kind}:${entry.id}`,
    kind: "connection-update",
    title: updateNoticeTitle(entry),
    to: connectionLink(entry.kind, entry.id),
    action: "Open",
    data: { kind: entry.kind, id: entry.id }
  });

describe("a newer version of a connected app is a notice in the bell (#1118)", () => {
  it("says which app, which version is out and which one you have", () => {
    expect(updateNoticeTitle(qbittorrent)).toBe("qBittorrent 5.0.2 is out · you have 4.6.7");
  });

  it("is one notice per app, listed apart from the problems, opening that app's connection", () => {
    const summary = summaryOf([], [noticeRow(qbittorrent)]);
    const rows = bellRows(summary);

    // Ready, not a problem: it is in no number (an update ready is the blue dot).
    expect(summary.count).toBe(0);
    expect(rows.deluno).toEqual([]);
    const row = rows.updates.find((entry) => entry.kind === "connection-update");
    expect(row?.title).toBe("qBittorrent 5.0.2 is out · you have 4.6.7");
    expect(row?.to).toBe("/indexers/download-clients?connection=qb1");
    expect(row?.actions.map((action) => action.label)).toEqual(["Open", "Dismiss"]);
    expect(row?.actions[1]?.request?.path).toBe("/api/connections/downloadClient/qb1/version/dismiss");
    for (const action of row?.actions ?? []) expect(action.label.length).toBeLessThanOrEqual(ALERT_LABEL_MAX);
  });

  it("is gone once dismissed, because the server no longer lists it", () => {
    expect(bellRows(summaryOf([], [])).updates).toEqual([]);
  });

  it("comes back for a newer version than the one dismissed", () => {
    const rows = bellRows(summaryOf([], [noticeRow({ ...qbittorrent, latestVersion: "5.0.3", dismissedVersion: "5.0.2", notice: true })]));

    expect(rows.updates.map((row) => row.title)).toEqual(["qBittorrent 5.0.3 is out · you have 4.6.7"]);
  });

  it("gives every kind of connection a place to open", () => {
    expect(connectionLink("processor", "w1")).toBe("/settings/processing?connection=w1");
    expect(connectionLink("mediaServer", "p1")).toBe("/indexers/media-servers?connection=p1");
    expect(connectionLink("requests", "s1")).toBe("/indexers/media-servers?connection=s1");
  });
});
