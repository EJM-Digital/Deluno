import { describe, expect, it } from "vitest";
import { parseImportJobPayload } from "./import-job-payload";

describe("what an import job is about", () => {
  // What a screen writes when somebody presses Queue import.
  it("reads a payload written in camelCase", () => {
    const info = parseImportJobPayload(
      JSON.stringify({ preview: { title: "Arrival", fileName: "Arrival.mkv", sourcePath: "C:\\Downloads\\Arrival.mkv" }, dispatchId: "dispatch-1" })
    );

    expect(info).toEqual({
      title: "Arrival",
      fileName: "Arrival.mkv",
      sourcePath: "C:\\Downloads\\Arrival.mkv",
      destinationPath: null,
      dispatchId: "dispatch-1"
    });
  });

  // What processor output writes. Reading only camelCase named every import of
  // a processed file "Job 01a0c89f", with no source and no destination (#564).
  it("reads the same payload written in PascalCase", () => {
    const info = parseImportJobPayload(
      JSON.stringify({
        Preview: { Title: "Big Buck Bunny", FileName: "Big.Buck.Bunny.mkv", SourcePath: "C:\\Weir3\\Refined\\Movies\\Big.Buck.Bunny.mkv" },
        DispatchId: "dispatch-2"
      })
    );

    expect(info?.title).toBe("Big Buck Bunny");
    expect(info?.fileName).toBe("Big.Buck.Bunny.mkv");
    expect(info?.sourcePath).toBe("C:\\Weir3\\Refined\\Movies\\Big.Buck.Bunny.mkv");
    expect(info?.dispatchId).toBe("dispatch-2");
  });

  it("answers nothing for a payload it cannot read", () => {
    expect(parseImportJobPayload(null)).toBeNull();
    expect(parseImportJobPayload("not json")).toBeNull();
    expect(parseImportJobPayload("{}")).toEqual({
      title: null,
      fileName: null,
      sourcePath: null,
      destinationPath: null,
      dispatchId: null
    });
  });
});
