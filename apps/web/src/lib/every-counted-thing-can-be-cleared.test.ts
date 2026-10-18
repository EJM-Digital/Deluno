import { describe, expect, it } from "vitest";
import type { AttentionSummaryRow } from "./attention-summary";
import { serverRow, summaryOf } from "./attention-summary-fixtures";
import { bellRows } from "./bell-rows";

/**
 * Every problem Deluno counts can be cleared from a screen (owner's rule): each kind of row the attention summary can hold
 * is drawn by the bell with a button that closes it, silences it or fixes it at the source. A kind with only a link to look
 * at would be a count nobody can bring down. The table is the summary's kinds (AttentionSummaryBuilder) one by one.
 */
type Clearing = { row: AttentionSummaryRow; buttons: string[] };

const TABLE: Record<string, Clearing> = {
  "a stalled download with a finding to silence": {
    row: serverRow({ id: "stuck:qb:t1", kind: "stuck", section: "media", data: { id: "t1", clientId: "qb", clientName: "qBittorrent", title: "Sintel", releaseName: "Sintel.1080p", status: "stalled", errorMessage: null, healthFindings: [{ kind: "stalled", summary: "Not moving." }] } }),
    buttons: ["Find another", "Dismiss"]
  },
  "an import case that needs a decision": {
    row: serverRow({ id: "recovery:movie:c1", kind: "recovery", section: "media", data: { id: "c1", mediaType: "movie", title: "Sintel", failureKind: "importFailed", detailsJson: JSON.stringify({ DispatchId: "d1" }) } }),
    buttons: ["Retry", "Dismiss"]
  },
  "a failed import job": {
    row: serverRow({ id: "import:j1", kind: "import", section: "media", data: { id: "j1" } }),
    buttons: ["Try again", "Dismiss"]
  },
  "a hand-off to the processor that failed": {
    row: serverRow({ id: "handoff:h1", kind: "handoff", section: "media", data: { id: "h1", releaseName: "Sintel.1080p", status: "failed" } }),
    buttons: ["Retry", "Dismiss"]
  },
  "a wanted title no search could satisfy": {
    row: serverRow({ id: "no-release:movie:m1", kind: "no-release", section: "media", data: { id: "m1", mediaType: "movie", title: "Sintel", libraryId: "lib-1" } }),
    buttons: ["Search now", "Dismiss"]
  },
  "an indexer that is not answering": {
    row: serverRow({ id: "indexer:i1", kind: "connection", data: { id: "i1", name: "EZTV" } }),
    buttons: ["Test again", "Switch off"]
  },
  "a download client that is not answering": {
    row: serverRow({ id: "client:c1", kind: "connection", data: { id: "c1", name: "qBittorrent" } }),
    buttons: ["Test again", "Switch off"]
  },
  "a health problem": {
    row: serverRow({ id: "health:tags:unused", kind: "health:tags", data: { id: "tags:unused", name: "Unused tags", category: "tags", link: "/settings/tags" } }),
    buttons: ["Open Settings", "Snooze 1 day"]
  },
  "jobs that gave up": {
    row: serverRow({ id: "failed-jobs", kind: "jobs", tone: "bad" }),
    buttons: ["Retry", "Review"]
  }
};

describe("every kind of row the attention summary counts has a button that clears it (owner's rule)", () => {
  it.each(Object.entries(TABLE))("%s", (_name, { row, buttons }) => {
    const drawn = bellRows(summaryOf([row]));

    const bellRow = [...drawn.media, ...drawn.deluno][0]!;
    expect(bellRow.actions.map((action) => action.label)).toEqual(buttons);
    // Something that closes it: a request that dismisses, silences, retries or switches off, or a button to the screen that does.
    expect(bellRow.actions.every((action) => action.request !== undefined || action.to !== undefined)).toBe(true);
  });

  it("covers every kind the server builds, so a new kind cannot arrive without a way to clear it", () => {
    const kinds = new Set(Object.values(TABLE).map(({ row }) => row.kind.split(":")[0]));

    expect([...kinds].sort()).toEqual(["connection", "handoff", "health", "import", "jobs", "no-release", "recovery", "stuck"]);
  });

  it("a counted problem with a Dismiss dismisses by a request of its own, never a link", () => {
    for (const { row, buttons } of Object.values(TABLE)) {
      if (!buttons.includes("Dismiss")) continue;
      const dismissal = [...bellRows(summaryOf([row])).media][0]!.actions.find((action) => action.label === "Dismiss")!;
      expect(dismissal.request?.method).toBe("POST");
      expect(dismissal.request?.path).toMatch(/dismiss|ignore/);
    }
  });
});
