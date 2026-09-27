/**
 * Per-subtitle timing actions, shared between a movie's own file and one
 * episode of a show — the same two endpoints exist on both catalogues
 * (`POST .../subtitles/{language}/resync` and `.../reject`), so the button
 * behind them is written once (#842, #922).
 *
 * "Resync now" runs the timing logic synchronously and reports what it found
 * — not the bulk library button's "queued", a real answer to a real click.
 * "This is out of sync" is a person's own judgment overriding an
 * inconclusive automatic read: it blocklists the file and asks for the next
 * one, the same mechanism #897's automatic reject uses.
 */
import { useState } from "react";
import { AlertTriangle, RefreshCw } from "lucide-react";
import { Button } from "../ui/button";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { toast } from "../shell/toaster";
import { authedFetch } from "../../lib/use-auth";
import { describeRequestFailure } from "../../lib/search-reasons";

export function SubtitleTimingActions({
  basePath,
  language,
  timingStatus,
  onChanged
}: {
  /** e.g. `/api/movies/{id}` or `/api/series/episodes/{episodeId}` */
  basePath: string;
  language: string;
  timingStatus?: string | null;
  onChanged?: () => void;
}) {
  const [busy, setBusy] = useState<"resync" | "reject" | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);

  async function resyncNow() {
    setBusy("resync");
    let response: Response | null = null;
    try {
      response = await authedFetch(`${basePath}/subtitles/${language}/resync`, { method: "POST" });
      const payload = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error("subtitle-resync-failed");
      toast.success(payload?.message ?? "The subtitle was resynced.");
      onChanged?.();
    } catch (error) {
      const explained = await describeRequestFailure(response, error, { action: "resync this subtitle" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusy(null);
    }
  }

  async function markOutOfSync() {
    setBusy("reject");
    let response: Response | null = null;
    try {
      response = await authedFetch(`${basePath}/subtitles/${language}/reject`, { method: "POST" });
      const payload = (await response.json().catch(() => null)) as { message?: string } | null;
      if (!response.ok) throw new Error("subtitle-reject-failed");
      toast.success(payload?.message ?? "That subtitle was removed and blocklisted.");
      setConfirmOpen(false);
      onChanged?.();
    } catch (error) {
      const explained = await describeRequestFailure(response, error, { action: "blocklist this subtitle" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="flex items-center justify-end gap-1.5">
      <Button
        type="button"
        variant="ghost"
        size="sm"
        disabled={busy !== null}
        aria-label={`Resync ${language.toUpperCase()} subtitle now`}
        onClick={() => void resyncNow()}
      >
        <RefreshCw className={busy === "resync" ? "h-3.5 w-3.5 animate-spin" : "h-3.5 w-3.5"} />
        Resync now
      </Button>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        disabled={busy !== null}
        aria-label={`Mark the ${language.toUpperCase()} subtitle as out of sync`}
        onClick={() => setConfirmOpen(true)}
      >
        <AlertTriangle className="h-3.5 w-3.5" />
        Out of sync
      </Button>

      <ConfirmDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        title="This subtitle is out of sync?"
        description={
          timingStatus === "not_checked"
            ? "Deluno could not tell either way, and left it exactly as it was. Marking it out of sync removes it, blocklists it for this file, and fetches the next best one."
            : "This removes the subtitle, blocklists it for this file, and fetches the next best one."
        }
        confirmLabel="Yes, it's out of sync"
        confirmVariant="destructive"
        busy={busy === "reject"}
        onConfirm={() => void markOutOfSync()}
      />
    </div>
  );
}
