import { describe, expect, it } from "vitest";
import { computeAttention } from "../routes/dashboard/attention";
import { emptySummary } from "../routes/dashboard/summary-fixtures";
import { ALERT_LABEL_MAX } from "./alert-actions";
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

const attentionWith = (updates: ConnectionUpdate[]) => computeAttention(emptySummary(), { connectionUpdates: updates });

describe("a newer version of a connected app is a notice in the bell (#1118)", () => {
  it("says which app, which version is out and which one you have", () => {
    expect(updateNoticeTitle(qbittorrent)).toBe("qBittorrent 5.0.2 is out · you have 4.6.7");
  });

  it("is one notice per app, counted with the System problems, opening that app's connection", () => {
    const attention = attentionWith([qbittorrent]);

    expect(attention.counts.system).toBe(1);
    const row = bellRows(attention).deluno.find((entry) => entry.kind === "connection-update");
    expect(row?.title).toBe("qBittorrent 5.0.2 is out · you have 4.6.7");
    expect(row?.to).toBe("/indexers/download-clients?connection=qb1");
    expect(row?.actions.map((action) => action.label)).toEqual(["Open", "Dismiss"]);
    expect(row?.actions[1]?.request?.path).toBe("/api/connections/downloadClient/qb1/version/dismiss");
    for (const action of row?.actions ?? []) expect(action.label.length).toBeLessThanOrEqual(ALERT_LABEL_MAX);
  });

  it("is gone once dismissed, because the server says it is no longer a notice", () => {
    const attention = attentionWith([{ ...qbittorrent, notice: false, dismissedVersion: "5.0.2" }]);

    expect(attention.counts.system).toBe(0);
    expect(bellRows(attention).deluno).toEqual([]);
  });

  it("comes back for a newer version than the one dismissed", () => {
    const attention = attentionWith([{ ...qbittorrent, latestVersion: "5.0.3", dismissedVersion: "5.0.2", notice: true }]);

    expect(bellRows(attention).deluno.map((row) => row.title)).toEqual(["qBittorrent 5.0.3 is out · you have 4.6.7"]);
  });

  it("clears itself when a check reads the new version, because the app is then no longer listed", () => {
    expect(attentionWith([qbittorrent]).counts.system).toBe(1);
    expect(attentionWith([]).counts.system).toBe(0);
  });

  it("gives every kind of connection a place to open", () => {
    expect(connectionLink("processor", "w1")).toBe("/settings/processing?connection=w1");
    expect(connectionLink("mediaServer", "p1")).toBe("/indexers/media-servers?connection=p1");
    expect(connectionLink("requests", "s1")).toBe("/indexers/media-servers?connection=s1");
  });
});
