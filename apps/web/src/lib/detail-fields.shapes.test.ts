import { describe, expect, it } from "vitest";
import { ACTIVITY_PROBLEM_WORDS, detailFields, fixLinkForActivity, isProblemActivity } from "./detail-fields";

const fields = (value: unknown) => detailFields(JSON.stringify(value));

describe("what an entry's saved details say", () => {
  it("shows nothing for nothing", () => {
    expect(detailFields(null)).toEqual([]);
    expect(detailFields(undefined)).toEqual([]);
    expect(detailFields("")).toEqual([]);
    expect(detailFields("   ")).toEqual([]);
  });

  it("shows text that is not JSON as one note, exactly as it was", () => {
    expect(detailFields(" not json ")).toEqual([{ label: "Note", value: " not json " }]);
  });

  it("shows JSON that is a single value as a note", () => {
    expect(detailFields("42")).toEqual([{ label: "Note", value: "42" }]);
    expect(detailFields("true")).toEqual([{ label: "Note", value: "true" }]);
    expect(detailFields("\"hello\"")).toEqual([{ label: "Note", value: "hello" }]);
    expect(detailFields("null")).toEqual([{ label: "Note", value: "null" }]);
  });

  it("shows a list as one line of its parts, leaving out what holds nothing", () => {
    expect(fields(["a", " ", null, 2, true, false, { x: 1 }, ["b", "c"]])).toEqual([{ label: "Details", value: "a; 2; Yes; No; b; c" }]);
    expect(fields([])).toEqual([]);
    expect(fields([null, ""])).toEqual([]);
  });

  it("shows each fact under its plain label, with ids, paths and hashes in the fixed-width font", () => {
    expect(fields({ dispatchId: "d1", releaseName: "Dune", sourcePath: "C:\\in", torrentHash: "abc", folderName: "x", attempts: 3, dryRun: false, note: "plain" })).toEqual([
      { label: "Download", value: "d1", mono: true },
      { label: "Release", value: "Dune" },
      { label: "Source folder", value: "C:\\in", mono: true },
      { label: "Torrent hash", value: "abc", mono: true },
      { label: "Folder name", value: "x", mono: true },
      { label: "Attempts", value: "3" },
      { label: "Practice run", value: "No" },
      { label: "Note", value: "plain" }
    ]);
  });

  it("treats a key that is only an id, or ends in Id or _id, as an id", () => {
    expect(fields({ id: "1", customId: "2", other_id: "3", identity: "4", bid: "5" })).toEqual([
      { label: "Id", value: "1", mono: true },
      { label: "Custom id", value: "2", mono: true },
      { label: "Other id", value: "3", mono: true },
      { label: "Identity", value: "4" },
      { label: "Bid", value: "5" }
    ]);
  });

  it("knows its labels whatever the spelling of the key", () => {
    expect(fields({ "release_name": "A", "Failure-Message": "B", "OutputPath": "C" })).toEqual([
      { label: "Release", value: "A" },
      { label: "What went wrong", value: "B" },
      { label: "Output folder", value: "C", mono: true }
    ]);
  });

  it("makes a label of a key it does not know, in plain words", () => {
    expect(fields({ someNewFact: "x", "another_one-here.too": "y", V2Thing: "z" })).toEqual([
      { label: "Some new fact", value: "x" },
      { label: "Another one here too", value: "y" },
      { label: "V2 thing", value: "z" }
    ]);
  });

  it("keeps a key that has no letters or digits as it is", () => {
    expect(fields({ "___": "x" })).toEqual([{ label: "___", value: "x" }]);
  });

  it("leaves out values that carry nothing", () => {
    expect(fields({ a: null, b: "", c: "  ", d: [], e: [null], f: undefined })).toEqual([]);
  });

  it("shows nested facts under their parent's label, two levels down and no further", () => {
    expect(fields({ client: { name: "qB", host: { port: 8080, deeper: { gone: "x" } } } })).toEqual([
      { label: "Client name", value: "qB" },
      { label: "Client host port", value: "8080" }
    ]);
  });

  it("says one fact said twice only once", () => {
    expect(fields({ clientId: "c1", downloadClientId: "c1", downloadClientName: "qB", clientName: "qB" })).toEqual([
      { label: "Download client", value: "c1", mono: true },
      { label: "Download client", value: "qB" },
      { label: "Client name", value: "qB" }
    ]);
    expect(fields({ downloadClientId: "c1", clientId: "c2" })).toHaveLength(2);
  });
});

describe("the plain labels", () => {
  it.each([
    ["dispatchId", "Download"], ["handoffId", "Hand-off"], ["releaseName", "Release"], ["downloadClientId", "Download client"], ["downloadClientName", "Download client"],
    ["clientId", "Download client"], ["queueItemId", "Item in the client"], ["mediaType", "Kind"], ["entityId", "Title"], ["libraryId", "Library"],
    ["processorName", "Processor"], ["failureMessage", "What went wrong"], ["importFailureMessage", "What went wrong"], ["importFailureCode", "Reason code"],
    ["sourcePath", "Source folder"], ["outputPath", "Output folder"], ["destinationPath", "Destination"], ["attempts", "Attempts"], ["actions", "What Deluno did"], ["dryRun", "Practice run"]
  ])("%s is called %s", (key, label) => {
    expect(fields({ [key]: "x" })[0].label).toBe(label);
  });

  it("turns a run of separators into one space", () => {
    expect(fields({ "a__b--c..d": "x" })[0].label).toBe("A b c d");
  });

  it("does not take a word that merely contains id for an id", () => {
    expect(fields({ Identity: "x", my_idea: "y", ValidItem: "z" }).map((f) => f.mono)).toEqual([undefined, undefined, undefined]);
  });
});

describe("which Activity kinds are red", () => {
  it("is every kind with a problem word in it, in any case and with spaces around it", () => {
    for (const word of ACTIVITY_PROBLEM_WORDS) expect(isProblemActivity(`x.${word}.y`)).toBe(true);
    expect(isProblemActivity("  PROCESSING.FAILED ")).toBe(true);
  });

  it("is not a kind that says nothing wrong, or that says it was put right", () => {
    expect(isProblemActivity("download.grabbed")).toBe(false);
    expect(isProblemActivity("health.problem.resolved")).toBe(false);
    expect(isProblemActivity("outage.recovered")).toBe(false);
    expect(isProblemActivity("library.reachable")).toBe(false);
    expect(isProblemActivity(" Library.Reachable ")).toBe(false);
    expect(isProblemActivity("library.unreachable")).toBe(true);
  });
});

describe("where a red entry can be put right", () => {
  it("has no fix for an entry that did not fail", () => {
    expect(fixLinkForActivity("download.grabbed")).toBeNull();
    expect(fixLinkForActivity("download.grabbed", "ok")).toBeNull();
  });

  it("sends a failed download, import, hand-off, file or job to In progress", () => {
    for (const kind of ["processing.failed", "download.failed", "import.failed", "handoff.failed", "filesystem.failed", "job.failed", "processor.failed", "DOWNLOAD.FAILED"]) {
      expect(fixLinkForActivity(kind)).toEqual({ to: "/queue", label: "Fix under In progress" });
    }
  });

  it("sends a failed indexer or tracker to Find & Download", () => {
    expect(fixLinkForActivity("indexer.timeout")).toEqual({ to: "/indexers", label: "Fix it in Find & Download" });
    expect(fixLinkForActivity("trackers.failed")).toEqual({ to: "/indexers", label: "Fix it in Find & Download" });
  });

  it("sends anything else that is red to System", () => {
    expect(fixLinkForActivity("backup.failed")).toEqual({ to: "/system", label: "Fix it in System" });
    expect(fixLinkForActivity("x.processing.failed")).toEqual({ to: "/system", label: "Fix it in System" });
  });

  it("goes by the server's own verdict when it has one", () => {
    expect(fixLinkForActivity("download.grabbed", "problem")).toEqual({ to: "/queue", label: "Fix under In progress" });
    expect(fixLinkForActivity("odd.kind", "problem")).toEqual({ to: "/system", label: "Fix it in System" });
    expect(fixLinkForActivity("download.failed", "ok")).toEqual({ to: "/queue", label: "Fix under In progress" });
    expect(fixLinkForActivity("download.failed", null)).toEqual({ to: "/queue", label: "Fix under In progress" });
  });
});
