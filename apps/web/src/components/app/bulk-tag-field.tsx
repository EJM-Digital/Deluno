import type { BulkTagMode } from "../../hooks/use-bulk-edit";
import { Field } from "../ui/field";
import { Select } from "../ui/select";
import { TagPicker } from "./tag-picker";

/**
 * The one wording and control for bulk tag changes. The title bulk editor and
 * the connection lists (indexers, download clients, notifications) all use it,
 * so the three modes mean the same thing everywhere (#1044). The backend applies
 * them with the same MediaTagBulkOperations helper.
 */
export const BULK_TAG_MODES: { value: BulkTagMode; label: string }[] = [
  { value: "add", label: "Add" },
  { value: "remove", label: "Remove" },
  { value: "replace", label: "Replace" }
];

export const BULK_TAG_HELP =
  "Pick existing tags or create a new one. Add keeps existing tags; Remove drops only these; Replace wipes the rest.";

export function bulkTagActionLabel(mode: BulkTagMode) {
  return mode === "remove" ? "Remove tags" : mode === "replace" ? "Replace tags" : "Add tags";
}

export function BulkTagField({
  tags,
  mode,
  onTagsChange,
  onModeChange
}: {
  tags: string;
  mode: BulkTagMode;
  onTagsChange: (value: string) => void;
  onModeChange: (value: BulkTagMode) => void;
}) {
  return (
    <Field label="Tags" help={BULK_TAG_HELP}>
      <div className="flex items-center gap-2">
        <div className="min-w-0 flex-1">
          <TagPicker label="Tags to apply" value={tags} onChange={onTagsChange} warnUnused={false} />
        </div>
        <div className="w-32 shrink-0">
          <Select value={mode} onChange={(event) => onModeChange(event.target.value as BulkTagMode)} aria-label="How to apply the tags">
            {BULK_TAG_MODES.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
          </Select>
        </div>
      </div>
    </Field>
  );
}
