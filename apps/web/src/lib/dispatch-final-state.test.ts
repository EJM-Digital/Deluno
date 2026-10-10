import { describe, expect, it } from "vitest";
import type { DownloadDispatchItem } from "./api";
import { dispatchImportEnded, dispatchStanding, dispatchWasImported } from "./dispatch-final-state";

const sent = (over: Partial<DownloadDispatchItem> = {}) => ({ id: "d", status: "sent", ...over }) as DownloadDispatchItem;

describe("dispatchStanding (#1179)", () => {
  it("keeps a send still with the client in progress", () => {
    expect(dispatchStanding(sent())?.status).toBe("in-progress");
  });

  it("shows a send as imported once its download is imported, though its own status is still sent", () => {
    const standing = dispatchStanding(sent({ importStatus: "imported", importedFilePath: "C:\\TV\\Severance\\S02E01.mkv" }));
    expect(standing?.status).toBe("done");
    expect(standing?.pill.label).toBe("Imported");
    expect(standing?.detail).toBe("Imported as S02E01.mkv");
  });

  it("reads an import finished by time alone as imported", () => {
    expect(dispatchStanding(sent({ importCompletedUtc: "2026-10-08T05:00:00Z" }))?.pill.label).toBe("Imported");
  });

  it("shows a refused or blocked release as refused, not in progress", () => {
    expect(dispatchStanding(sent({ grabStatus: "refused" }))?.pill.label).toBe("Refused");
    expect(dispatchStanding(sent({ status: "blocked" }))?.pill.label).toBe("Refused");
  });

  it("shows a download taken out of the client as removed", () => {
    expect(dispatchStanding(sent({ status: "archived" }))?.pill.label).toBe("Removed");
    expect(dispatchStanding(sent({ status: "vanished" }))?.pill.label).toBe("Removed");
  });

  it("reads a failed send that was dismissed as closed, not failed (#1218)", () => {
    expect(dispatchStanding(sent({ importStatus: "failed", importFailureMessage: "The file check failed it." }))?.status).toBe("failed");
    const closed = dispatchStanding(sent({ status: "archived", importStatus: "failed", importFailureMessage: "The file check failed it." }));
    expect(closed?.status).toBe("done");
    expect(closed?.pill.label).toBe("Closed");
  });

  it("still reports a failed send as failed, and a planned one as waiting for a decision", () => {
    expect(dispatchStanding(sent({ status: "failed" }))?.status).toBe("failed");
    expect(dispatchStanding(sent({ importStatus: "failed" }))?.status).toBe("failed");
    expect(dispatchStanding(sent({ status: "planned" }))?.status).toBe("needs-decision");
  });

  it("a failed import never reads Imported, even with a completion time on the row (#1179)", () => {
    const done = "2026-10-08T05:00:00Z";
    const refused = dispatchStanding(sent({ importStatus: "failed", importCompletedUtc: done, importFailureMessage: "release refused; replacement search queued" }));
    expect(refused?.pill.label).toBe("Refused");
    const exists = dispatchStanding(sent({ importStatus: "failed", importCompletedUtc: done, importFailureMessage: "The destination file already exists." }));
    expect(exists?.pill.label).toBe("Failed");
    expect(exists?.status).toBe("failed");
    const removed = dispatchStanding(sent({ importStatus: "failed", importFailureMessage: "Download was removed from the client" }));
    expect(removed?.pill.label).toBe("Removed");
    expect(dispatchStanding(sent({ importStatus: "imported", importCompletedUtc: done }))?.pill.label).toBe("Imported");
  });
});

describe("one rule for imported (golden path rc.30.8)", () => {
  const refusedSample = sent({
    importStatus: "failed",
    importCompletedUtc: "2026-10-08T05:00:00Z",
    importFailureCode: "likelySample",
    importFailureMessage: "The detected runtime is under two minutes, so Deluno is treating this as a likely sample."
  });

  it("never calls a refused import imported, whatever time is on the row", () => {
    expect(dispatchWasImported(refusedSample)).toBe(false);
    expect(dispatchImportEnded(refusedSample)).toBe(true);
    expect(dispatchWasImported(sent({ importCompletedUtc: "2026-10-08T05:00:00Z", importFailureMessage: "refused" }))).toBe(false);
  });

  it("reads Imported on a real import only", () => {
    expect(dispatchWasImported(sent({ importStatus: "imported" }))).toBe(true);
    expect(dispatchImportEnded(sent())).toBe(false);
  });
});

describe("an extra copy of a release already in the library (#1202)", () => {
  const held = sent({ id: "a", entityId: "m1", releaseName: "Sintel.2010.1080p", importStatus: "imported" });

  it("is not left in progress when the same release is imported for the same title", () => {
    const extra = sent({ id: "b", entityId: "m1", releaseName: "sintel.2010.1080P" });
    const standing = dispatchStanding(extra, [extra, held]);
    expect(standing?.status).toBe("done");
    expect(standing?.pill.label).toBe("Not needed");
  });

  it("stays in progress for another title or another release", () => {
    expect(dispatchStanding(sent({ id: "c", entityId: "m2", releaseName: "Sintel.2010.1080p" }), [held])?.status).toBe("in-progress");
    expect(dispatchStanding(sent({ id: "d2", entityId: "m1", releaseName: "Sintel.2010.2160p" }), [held])?.status).toBe("in-progress");
  });
});
