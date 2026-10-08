import { describe, expect, it } from "vitest";
import { IMPORT_WORKFLOW_HELP, IMPORT_WORKFLOW_LABELS, importWorkflowLabel } from "./import-workflow";

describe("import workflow names (#1158)", () => {
  it("calls the second choice Refine before import, the name Weir and the docs use", () => {
    expect(IMPORT_WORKFLOW_LABELS["refine-before-import"]).toBe("Refine before import");
    expect(importWorkflowLabel("refine-before-import")).toBe("Refine before import");
    expect(importWorkflowLabel("standard")).toBe("Import immediately");
    expect(importWorkflowLabel(undefined)).toBe("Import immediately");
  });

  it("never uses the older names", () => {
    const said = JSON.stringify([IMPORT_WORKFLOW_LABELS, IMPORT_WORKFLOW_HELP]);
    expect(said).not.toMatch(/Wait for processing|Process, then import/);
  });
});
