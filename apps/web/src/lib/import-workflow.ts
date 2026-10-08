/**
 * The one name for each way a library imports (#1158). Deluno's docs and capability map call the second
 * one refine-before-import, and so does Weir, so every screen says exactly that.
 */
export const IMPORT_WORKFLOW_LABELS = {
  standard: "Import immediately",
  "refine-before-import": "Refine before import"
} as const;

/** Plain words for what each choice does, shown beside the choice. */
export const IMPORT_WORKFLOW_HELP = {
  standard: "Deluno imports the finished download as it is.",
  "refine-before-import": "Deluno waits for your processor to clean the download, then imports the cleaned file."
} as const;

export function importWorkflowLabel(importWorkflow: string | null | undefined): string {
  return importWorkflow === "refine-before-import" ? IMPORT_WORKFLOW_LABELS["refine-before-import"] : IMPORT_WORKFLOW_LABELS.standard;
}
