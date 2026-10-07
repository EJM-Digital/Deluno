import { describe, expect, it } from "vitest";
import { processorProductName } from "./settings-media-management-page-v2";

describe("processorProductName (#1144)", () => {
  it("takes the processor's own name from its connection name", () => {
    expect(processorProductName("Weir on WIN-PC")).toBe("Weir");
    expect(processorProductName("FileFlows on NAS")).toBe("FileFlows");
  });

  it("says nothing for the generic form", () => {
    expect(processorProductName("Processor on WIN-PC")).toBeNull();
  });
});
