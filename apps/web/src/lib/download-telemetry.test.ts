import { describe, expect, it } from "vitest";
import { CLIENT_HISTORY_OUTCOMES, clientHistoryOutcomeLabel, downloadQueueStatuses, isImportReadyStatus, isProcessingStatus, queueStatusLabel } from "./download-telemetry";

describe("clientHistoryOutcomeLabel", () => {
  it.each(CLIENT_HISTORY_OUTCOMES)(
    "has plain words for the known client-history outcome %s",
    (outcome) => {
      const words = clientHistoryOutcomeLabel(outcome);
      // A missing entry falls back to the raw code, which is the exact leak
      // #948 was filed over — "needsAttention" on screen instead of words.
      expect(words).not.toBe(outcome);
      expect(words.length).toBeGreaterThan(0);
    }
  );

  it("falls back to the raw code for an outcome nobody has answered yet", () => {
    expect(clientHistoryOutcomeLabel("somethingBrandNew")).toBe("somethingBrandNew");
  });
});

describe("a download imported earlier whose file has since left the library (#1182)", () => {
  const status = downloadQueueStatuses.importedFileLeft;

  it("says so in plain words, never In library or Imported", () => {
    expect(queueStatusLabel(status)).toBe("Imported earlier, but the file has since left the library");
  });

  it("is history: not ready to import and not being processed", () => {
    expect(isImportReadyStatus(status)).toBe(false);
    expect(isProcessingStatus(status)).toBe(false);
  });
});

describe("a download whose files are gone (#1200)", () => {
  const status = downloadQueueStatuses.filesGone;

  it("says so in plain words", () => {
    expect(queueStatusLabel(status)).toBe("Files are gone");
  });

  it("is never ready to import and not being processed", () => {
    expect(isImportReadyStatus(status)).toBe(false);
    expect(isProcessingStatus(status)).toBe(false);
  });
});
