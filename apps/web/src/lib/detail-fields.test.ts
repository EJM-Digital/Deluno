import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { ACTIVITY_PROBLEM_WORDS, detailFields, fixLinkForActivity } from "./detail-fields";

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

// #1151: the red entries "Could not tell the processor what happened" and a dead-lettered job had no fix link.
describe("fixLinkForActivity covers every red Activity kind", () => {
  const root = resolve(__dirname, "../../../../src/Deluno.Contracts");
  const labels = readFileSync(resolve(root, "ActivityEventLabels.cs"), "utf8");
  const history = readFileSync(resolve(root, "ActivityHistory.cs"), "utf8");
  const categories = [...labels.matchAll(/\["([^"]+)"\]\s*=\s*"/g)].map((match) => match[1]);

  it("reads the server's list of kinds and red words", () => {
    expect(categories.length).toBeGreaterThan(150);
    const block = history.slice(history.indexOf("ProblemWords ="), history.indexOf("];", history.indexOf("ProblemWords =")));
    const serverWords = [...block.matchAll(/"([^"]+)"/g)].map((match) => match[1]);
    expect([...ACTIVITY_PROBLEM_WORDS].sort()).toEqual(serverWords.sort());
  });

  it("finds a link for every kind the server marks as a problem", () => {
    const server = (category: string) => {
      const lower = category.toLowerCase();
      if (lower.includes("resolved") || lower.includes("recovered") || lower === "library.reachable") return false;
      return ACTIVITY_PROBLEM_WORDS.some((word) => lower.includes(word));
    };
    const red = categories.filter(server);
    expect(red.length).toBeGreaterThan(40);
    const without = red.filter((category) => fixLinkForActivity(category, "problem") === null || fixLinkForActivity(category) === null);
    expect(without).toEqual([]);
  });

  it("links the two entries that had none", () => {
    expect(fixLinkForActivity("processing.outcome-not-reported")?.to).toBe("/queue");
    expect(fixLinkForActivity("job.dead-letter")?.to).toBe("/queue");
  });

  it("still links nothing for entries that went well", () => {
    for (const category of ["job.completed", "filesystem.import.completed", "health.resolved", "download.vanished.recovered", "library.reachable"]) {
      expect(fixLinkForActivity(category)).toBeNull();
    }
  });
});
