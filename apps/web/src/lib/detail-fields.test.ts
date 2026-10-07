import { describe, expect, it } from "vitest";
import { detailFields, fixLinkForActivity } from "./detail-fields";

describe("detailFields", () => {
  it("shows each fact under a plain label, never the JSON", () => {
    const fields = detailFields(JSON.stringify({ DispatchId: "d1", ReleaseName: "Oppenheimer.2023", DownloadClientName: "Deluge", MediaType: "movies", EntityId: "" }));

    expect(fields).toEqual([
      { label: "Download", value: "d1", mono: true },
      { label: "Release", value: "Oppenheimer.2023" },
      { label: "Download client", value: "Deluge" },
      { label: "Kind", value: "movies" }
    ]);
  });

  it("reads lists and yes/no values as words", () => {
    expect(detailFields(JSON.stringify({ actions: ["release refused", "replacement search queued"], dryRun: false }))).toEqual([
      { label: "What Deluno did", value: "release refused; replacement search queued" },
      { label: "Practice run", value: "No" }
    ]);
  });

  it("shows text that is not JSON as a note", () => {
    expect(detailFields("the file was moved")).toEqual([{ label: "Note", value: "the file was moved" }]);
  });

  it("has nothing to show for empty details", () => {
    expect(detailFields(null)).toEqual([]);
    expect(detailFields("{}")).toEqual([]);
  });
});

describe("fixLinkForActivity", () => {
  it("links a failed hand-off to Transfers", () => {
    expect(fixLinkForActivity("processing.submission-failed")).toEqual({ to: "/queue", label: "Fix it in Transfers" });
  });

  it("links nothing for an entry that did not fail", () => {
    expect(fixLinkForActivity("download.import.completed")).toBeNull();
  });
});
