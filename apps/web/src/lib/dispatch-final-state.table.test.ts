import { describe, expect, it } from "vitest";
import type { DownloadDispatchItem } from "./api";
import { CONTAINED_SEND_CODE, dispatchImportEnded, dispatchImportFailed, dispatchIsExtraCopy, dispatchStanding, dispatchWasImported } from "./dispatch-final-state";

const send = (overrides: Partial<DownloadDispatchItem> = {}): DownloadDispatchItem =>
  ({ id: "d1", entityId: "m1", releaseName: "Dune.2021", status: "sent", grabStatus: null, importStatus: null, importCompletedUtc: null, importFailureCode: null, importFailureMessage: null, importedFilePath: null, grabMessage: null, ...overrides }) as unknown as DownloadDispatchItem;

describe("whether the import of a send ended badly", () => {
  it.each(["failed", "importFailed", " FAILED ", "ImportFailed"])("treats %j as failed", (importStatus) => {
    expect(dispatchImportFailed(send({ importStatus }))).toBe(true);
  });

  it.each([null, "", "imported", "completed", "pending"])("does not treat %j as failed", (importStatus) => {
    expect(dispatchImportFailed(send({ importStatus }))).toBe(false);
  });
});

describe("whether a send was imported", () => {
  it.each(["imported", "completed", " Imported "])("is imported when the import says %j", (importStatus) => {
    expect(dispatchWasImported(send({ importStatus }))).toBe(true);
  });

  it("is never imported when the import failed, whatever time is on the row", () => {
    expect(dispatchWasImported(send({ importStatus: "failed", importCompletedUtc: "2026-11-04T10:00:00Z" }))).toBe(false);
  });

  it("is not imported when the import says anything else, even with a completion time", () => {
    expect(dispatchWasImported(send({ importStatus: "pending", importCompletedUtc: "2026-11-04T10:00:00Z" }))).toBe(false);
  });

  it("is imported on the completion time alone when nothing says otherwise", () => {
    expect(dispatchWasImported(send({ importCompletedUtc: "2026-11-04T10:00:00Z" }))).toBe(true);
  });

  it.each([
    ["no completion time", {}],
    ["a failure code", { importCompletedUtc: "2026-11-04T10:00:00Z", importFailureCode: "x" }],
    ["a failure message", { importCompletedUtc: "2026-11-04T10:00:00Z", importFailureMessage: "x" }],
    ["a failed send", { importCompletedUtc: "2026-11-04T10:00:00Z", status: "failed" }],
    ["an import-failed send", { importCompletedUtc: "2026-11-04T10:00:00Z", status: "importFailed" }],
    ["a failed grab", { importCompletedUtc: "2026-11-04T10:00:00Z", grabStatus: "failed" }]
  ])("is not imported on the completion time with %s", (_name, overrides) => {
    expect(dispatchWasImported(send(overrides as Partial<DownloadDispatchItem>))).toBe(false);
  });
});

describe("whether nothing more is coming back for an import", () => {
  it("has ended when it was imported, failed or has a completion time, and not otherwise", () => {
    expect(dispatchImportEnded(send({ importStatus: "imported" }))).toBe(true);
    expect(dispatchImportEnded(send({ importStatus: "failed" }))).toBe(true);
    expect(dispatchImportEnded(send({ importStatus: "pending", importCompletedUtc: "2026-11-04T10:00:00Z" }))).toBe(true);
    expect(dispatchImportEnded(send())).toBe(false);
    expect(dispatchImportEnded(null)).toBe(false);
    expect(dispatchImportEnded(undefined)).toBe(false);
  });
});

describe("whether a send is an extra copy of something already in the library", () => {
  const imported = send({ id: "d2", importStatus: "imported" });

  it("is an extra copy when another send of the same release for the same title was imported", () => {
    expect(dispatchIsExtraCopy(send(), [send(), imported])).toBe(true);
    expect(dispatchIsExtraCopy(send({ releaseName: "  dune.2021 " }), [imported])).toBe(true);
  });

  it("is not when the other is a different title, a different release, the same send, or not imported", () => {
    expect(dispatchIsExtraCopy(send(), [{ ...imported, entityId: "m2" } as DownloadDispatchItem])).toBe(false);
    expect(dispatchIsExtraCopy(send(), [{ ...imported, releaseName: "Dune.2021.Remux" } as DownloadDispatchItem])).toBe(false);
    expect(dispatchIsExtraCopy(send({ id: "d2", importStatus: null }), [imported])).toBe(false);
    expect(dispatchIsExtraCopy(send(), [send({ id: "d2" })])).toBe(false);
    expect(dispatchIsExtraCopy(send(), [])).toBe(false);
  });

  it("is not an extra copy when it was itself imported or failed to import", () => {
    expect(dispatchIsExtraCopy(send({ importStatus: "imported" }), [imported])).toBe(false);
    expect(dispatchIsExtraCopy(send({ importStatus: "failed" }), [imported])).toBe(false);
  });
});

describe("where a send stands", () => {
  it("is closed, with the reason, when a failed send was dismissed", () => {
    expect(dispatchStanding(send({ status: "archived", importStatus: "failed", importFailureMessage: "Disk full" }))).toEqual({ status: "done", pill: { tone: "idle", label: "Closed" }, detail: "Disk full" });
    expect(dispatchStanding(send({ status: "archived", grabStatus: "failed", grabMessage: "Client said no" }))).toEqual({ status: "done", pill: { tone: "idle", label: "Closed" }, detail: "Client said no" });
    expect(dispatchStanding(send({ status: "archived", grabStatus: "failed" }))?.detail).toBe("Dismissed after it failed");
  });

  it("is refused when the import was refused or rejected, with the server's words or ours", () => {
    expect(dispatchStanding(send({ importStatus: "failed", importFailureCode: "refused-by-rule" }))).toEqual({ status: "done", pill: { tone: "warn", label: "Refused" }, detail: "The release was refused" });
    expect(dispatchStanding(send({ importStatus: "failed", importFailureMessage: "File rejected by the processor" }))?.detail).toBe("File rejected by the processor");
  });

  it("is removed when the import's file was taken away", () => {
    expect(dispatchStanding(send({ importStatus: "failed", importFailureMessage: "The file was removed" }))).toEqual({ status: "done", pill: { tone: "idle", label: "Removed" }, detail: "Taken out of the download client" });
    expect(dispatchStanding(send({ importStatus: "failed", importFailureCode: "vanished" }))?.pill.label).toBe("Removed");
  });

  it("is failed, with the message if there is one, when the import failed for any other reason", () => {
    expect(dispatchStanding(send({ importStatus: "failed", importFailureMessage: "Disk full" }))).toEqual({ status: "failed", pill: { tone: "bad", label: "Failed" }, detail: "Disk full" });
    expect(dispatchStanding(send({ importStatus: "failed" }))).toEqual({ status: "failed", pill: { tone: "bad", label: "Failed" }, detail: undefined });
  });

  it("is imported, naming the file when it is known", () => {
    expect(dispatchStanding(send({ importStatus: "imported", importedFilePath: "C:\\Movies\\Dune (2021)\\Dune.mkv" }))).toEqual({ status: "done", pill: { tone: "ok", label: "Imported" }, detail: "Imported as Dune.mkv" });
    expect(dispatchStanding(send({ importStatus: "imported", importedFilePath: "/movies/Dune/Dune.mkv" }))?.detail).toBe("Imported as Dune.mkv");
    expect(dispatchStanding(send({ importStatus: "imported" }))?.detail).toBe("Imported into the library");
  });

  it.each([["failed", null], ["importFailed", null], ["sent", "failed"]])("is failed for a send saying %s with a grab saying %s", (status, grabStatus) => {
    expect(dispatchStanding(send({ status, grabStatus }))).toEqual({ status: "failed", pill: { tone: "bad", label: "Failed" } });
  });

  it.each([["sent", "refused"], ["sent", "blocked"], ["sent", "rejected"], ["refused", null], ["blocked", null]])("is refused for a send saying %s with a grab saying %s", (status, grabStatus) => {
    expect(dispatchStanding(send({ status, grabStatus, grabMessage: "No thanks" }))).toEqual({ status: "done", pill: { tone: "warn", label: "Refused" }, detail: "No thanks" });
    expect(dispatchStanding(send({ status, grabStatus }))?.detail).toBe("The download client turned it down");
  });

  it("is not needed when Deluno contained it, with the reason on the record or ours", () => {
    expect(CONTAINED_SEND_CODE).toBe("handed-over-elsewhere");
    expect(dispatchStanding(send({ importFailureCode: "Handed-Over-Elsewhere", importFailureMessage: "Handed to another" }))).toEqual({ status: "done", pill: { tone: "idle", label: "Not needed" }, detail: "Handed to another" });
    expect(dispatchStanding(send({ importFailureCode: CONTAINED_SEND_CODE }))?.detail).toBe("Not needed: another copy was handed over");
  });

  it.each(["archived", "removed", "vanished", "cancelled"])("is removed for a send saying %s", (status) => {
    expect(dispatchStanding(send({ status }))).toEqual({ status: "done", pill: { tone: "idle", label: "Removed" }, detail: "Taken out of the download client" });
  });

  it("is not needed when it is an extra copy of something already imported", () => {
    expect(dispatchStanding(send(), [send(), send({ id: "d2", importStatus: "imported" })])).toEqual({
      status: "done", pill: { tone: "idle", label: "Not needed" }, detail: "The same release was already in the library, so this extra copy was not used."
    });
  });

  it("still needs a decision when planned, even if the same release is already in the library", () => {
    expect(dispatchStanding(send({ status: "planned" }), [send({ id: "d2", importStatus: "imported" })])?.pill.label).toBe("Needs a decision");
  });

  it("is in progress while sent, and needs a decision while planned", () => {
    expect(dispatchStanding(send())).toEqual({ status: "in-progress", pill: { tone: "info", label: "In progress" } });
    expect(dispatchStanding(send({ status: "planned" }))).toEqual({ status: "needs-decision", pill: { tone: "warn", label: "Needs a decision" } });
  });

  it("says nothing about a status it does not know", () => {
    expect(dispatchStanding(send({ status: "mystery" }))).toBeNull();
    expect(dispatchStanding(send({ status: null as never }))).toBeNull();
  });
});
