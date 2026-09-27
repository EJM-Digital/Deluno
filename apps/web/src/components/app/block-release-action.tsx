/**
 * "Block this release" — cleanup sheet J5 (#925).
 *
 * Wherever a release is named (a search result, a title's search history, an
 * Activity entry), this offers the same decision the Queue's "It's bad"
 * choice already makes when removing a download (#924): refuse the release
 * everywhere, and — when Deluno finds it still downloading — stop it and
 * delete its files first, using that exact path rather than a new one.
 *
 * Self-contained: renders its own trigger button and confirm dialog, checks
 * the download-client telemetry for a live match when it opens, and reports
 * back through `onBlocked` so the caller can refresh whatever list it came
 * from. Every instance's accessible name includes the release name, so a
 * list of several releases never has two controls announced the same way.
 */
import { useState } from "react";
import { ShieldBan } from "lucide-react";
import { Button } from "../ui/button";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { toast } from "../shell/toaster";
import { describeRequestFailure } from "../../lib/search-reasons";
import {
  blockReleaseOnly,
  checkQueueMatch,
  stopBlockAndSearchAgain,
  type QueueMatch,
  type ReleaseIdentity
} from "../../lib/block-release";

interface BlockReleaseActionProps extends ReleaseIdentity {
  /** Called after a successful block (either path), so the caller can revalidate. */
  onBlocked?: () => void;
  size?: "sm" | "default";
  variant?: "outline" | "destructive" | "ghost";
  className?: string;
}

export function BlockReleaseAction({
  onBlocked,
  size = "sm",
  variant = "outline",
  className,
  ...identity
}: BlockReleaseActionProps) {
  const [open, setOpen] = useState(false);
  const [checking, setChecking] = useState(false);
  const [match, setMatch] = useState<QueueMatch | null>(null);
  const [busy, setBusy] = useState(false);

  const displayTitle = identity.title || identity.releaseName;

  async function handleOpen() {
    setOpen(true);
    setMatch(null);
    setChecking(true);
    try {
      setMatch(await checkQueueMatch(identity));
    } finally {
      setChecking(false);
    }
  }

  async function handleJustBlock() {
    setBusy(true);
    try {
      const { response } = await blockReleaseOnly(identity);
      if (!response.ok) throw new Error("block-release-failed");
      toast.success(`"${displayTitle}" blocked; every indexer's copy is refused from now on.`);
      setOpen(false);
      onBlocked?.();
    } catch (error) {
      const explained = await describeRequestFailure(null, error, { action: "block this release" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusy(false);
    }
  }

  async function handleStopAndBlock() {
    if (!match) return;
    setBusy(true);
    try {
      const response = await stopBlockAndSearchAgain(match.clientId, match.queueItemId);
      if (!response.ok) throw new Error("stop-and-block-failed");
      toast.success(`"${displayTitle}" stopped in ${match.clientName}, its files deleted, blocked everywhere, and a replacement search started.`);
      setOpen(false);
      onBlocked?.();
    } catch (error) {
      const explained = await describeRequestFailure(null, error, { action: "stop and block this release" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusy(false);
    }
  }

  const description = match
    ? `Deluno found this release downloading in ${match.clientName} right now. Blocking it refuses every indexer's copy from now on; stopping it also removes it from ${match.clientName}, deletes its files, and starts a replacement search.`
    : `Refuses every indexer's copy of "${identity.releaseName}" from now on. Nothing already downloaded is touched.`;

  return (
    <>
      <Button
        type="button"
        variant={variant}
        size={size}
        className={className}
        aria-label={`Block "${displayTitle}"`}
        onClick={() => void handleOpen()}
      >
        <ShieldBan className="h-3.5 w-3.5" />
        Block this release
      </Button>

      <ConfirmDialog
        open={open}
        onOpenChange={(next) => {
          if (!busy) setOpen(next);
        }}
        title={`Block "${displayTitle}"?`}
        description={checking ? "Checking whether it's downloading right now…" : description}
        confirmLabel={match ? "Stop it, delete its files and find another" : "Block this release"}
        confirmVariant="destructive"
        secondaryLabel={match ? "Just block it" : undefined}
        secondaryVariant="outline"
        busy={busy || checking}
        onConfirm={() => void (match ? handleStopAndBlock() : handleJustBlock())}
        onSecondary={match ? () => void handleJustBlock() : undefined}
      />
    </>
  );
}
