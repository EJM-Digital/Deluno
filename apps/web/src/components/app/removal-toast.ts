import { fetchJson } from "../../lib/api";
import { describeRemovalOutcome, type RecycleBinRestoreResult } from "../../lib/recycle-bin-words";
import { toast } from "../shell/toaster";

/**
 * The toast after a title is removed (#1160). When files went to the recycle
 * bin it says so, says for how long, and offers Restore right there, which
 * brings back the title with its files (#1161).
 */
export function showRemovalToast(
  title: string,
  metadata: Record<string, string | null> | null | undefined,
  navigate: (to: string) => void
) {
  const words = describeRemovalOutcome(metadata);
  if (!words.itemIds.length) {
    toast.success(`${title} removed from Deluno`);
    return;
  }

  toast.success(`${title} removed from Deluno`, {
    description: words.description ?? undefined,
    duration: 15000,
    action: {
      label: "Restore",
      onClick: () => void restoreRemovedTitle(title, words.itemIds, navigate)
    }
  });
}

async function restoreRemovedTitle(title: string, itemIds: string[], navigate: (to: string) => void) {
  try {
    let opened: string | null = null;
    let failure: string | null = null;
    let note: string | null = null;
    for (const id of itemIds) {
      const result = await fetchJson<RecycleBinRestoreResult>(`/api/recycle-bin/${id}/restore`, { method: "POST" });
      note = result.message;
      if (result.titleRestored && result.titleId && !opened) {
        opened = `${result.titleEntityType === "series" ? "/tv" : "/movies"}/${result.titleId}`;
      }
      if (!result.success) failure = result.message;
    }

    if (failure) {
      toast.error(`${title} could not be restored`, { description: failure });
      return;
    }

    toast.success(`${title} restored`, { description: note ?? undefined });
    if (opened) navigate(opened);
  } catch {
    toast.error(`${title} could not be restored`, {
      description: "Open System, Recycle bin to restore it from there."
    });
  }
}
