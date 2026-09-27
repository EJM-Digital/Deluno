/**
 * The Queue's "why remove this" dialog (cleanup sheet J1/J2, #924), offered
 * wherever a download still sitting in a client is shown outside the Queue
 * itself — a title's dispatch history and search history
 * (movie-detail-page.tsx, show-detail-page.tsx) and Activity's "Sent to
 * downloads" list (download-dispatch-drawer.tsx).
 *
 * Matched against the live queue exactly the way block-release.ts matches a
 * release for its own "stop it" option: by hash first, then by release name.
 * A release the client is no longer holding has nothing to remove, so this
 * renders nothing until a match is found — never a button whose click can
 * only fail.
 */
import { useEffect, useState } from "react";
import { Trash2 } from "lucide-react";
import { Button } from "../ui/button";
import { RemovalReasonDialog, type RemovalReason } from "../ui/removal-reason-dialog";
import { toast } from "../shell/toaster";
import { checkQueueMatch, type QueueMatch, type ReleaseIdentity } from "../../lib/block-release";
import { submitQueueRemoval } from "../../lib/remove-download";

interface RemoveDownloadActionProps extends ReleaseIdentity {
  /** Called after a successful removal (any reason), so the caller can revalidate and close its own drawer. */
  onRemoved?: () => void;
  size?: "sm" | "default";
  variant?: "outline" | "destructive" | "ghost";
  className?: string;
}

export function RemoveDownloadAction({
  onRemoved,
  size = "sm",
  variant = "outline",
  className,
  ...identity
}: RemoveDownloadActionProps) {
  const [match, setMatch] = useState<QueueMatch | null>(null);
  const [checked, setChecked] = useState(false);
  const [open, setOpen] = useState(false);
  const [busyReason, setBusyReason] = useState<RemovalReason | null>(null);

  const displayTitle = identity.title || identity.releaseName;

  useEffect(() => {
    let cancelled = false;
    setChecked(false);
    void checkQueueMatch(identity).then((found) => {
      if (!cancelled) {
        setMatch(found);
        setChecked(true);
      }
    });
    return () => {
      cancelled = true;
    };
    // Only the identity that decides the match should re-run the check.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [identity.releaseName, identity.hash]);

  async function chooseReason(reason: RemovalReason) {
    if (!match) return;
    setBusyReason(reason);
    try {
      // The server does the unmonitor itself now when reason is "unwanted"
      // (#924), so there is nothing left to follow up here — the returned
      // message already says so.
      const result = await submitQueueRemoval(match.clientId, match.queueItemId, reason);
      toast.success(result.message);
      setOpen(false);
      onRemoved?.();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That removal could not be completed.");
    } finally {
      setBusyReason(null);
    }
  }

  // Not (or no longer) in a client: nothing here to remove from.
  if (!checked || !match) return null;

  return (
    <>
      <Button
        type="button"
        variant={variant}
        size={size}
        className={className}
        aria-label={`Remove "${displayTitle}" from ${match.clientName}`}
        onClick={() => setOpen(true)}
      >
        <Trash2 className="h-3.5 w-3.5" />
        Remove from client
      </Button>

      <RemovalReasonDialog
        open={open}
        onOpenChange={setOpen}
        title={displayTitle}
        busyReason={busyReason}
        onChoose={(reason) => void chooseReason(reason)}
      />
    </>
  );
}
