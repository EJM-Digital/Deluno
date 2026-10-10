import { describe, expect, it } from "vitest";
import { settleFormAfterSave } from "./settle-form-after-save";

describe("settleFormAfterSave", () => {
  const submitted = { name: "Films", rootPath: "/media/films", downloadsPath: "" };

  it("shows the server's tidied value for a field nobody touched since the save started", () => {
    const settled = { name: "Films", rootPath: "/media/films", downloadsPath: "/downloads" };
    expect(settleFormAfterSave(submitted, submitted, settled)).toEqual(settled);
  });

  it("keeps a name typed while the save was running", () => {
    const settled = { ...submitted, downloadsPath: "/downloads" };
    const typed = { ...submitted, name: "Films-b" };
    expect(settleFormAfterSave(typed, submitted, settled)).toEqual({ name: "Films-b", rootPath: "/media/films", downloadsPath: "/downloads" });
  });

  it("keeps a list edited while the save was running, as the profile, release-rule and tag forms hold lists", () => {
    const sent = { name: "Plan", indexerIds: ["a"], specifications: [{ value: "x" }] };
    const settled = { name: "Plan", indexerIds: ["a"], specifications: [{ value: "x" }] };
    const edited = { ...sent, indexerIds: ["a", "b"] };
    const result = settleFormAfterSave(edited, sent, settled);
    expect(result.indexerIds).toEqual(["a", "b"]);
    expect(result.specifications).toBe(settled.specifications);
  });
});
