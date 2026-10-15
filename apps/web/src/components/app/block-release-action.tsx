/**
 * "Block this release" — cleanup sheet J5 (#925).
 *
 * Wherever a release is named (a search result, a title's search history, an
 * Activity entry, the Queue), this offers the same decision the Queue's
 * "It's bad" choice already makes when removing a download (#924): refuse
 * the release everywhere, and — when Deluno finds it still downloading —
 * stop it and delete its files first, using that exact path rather than a
 * new one. When the release is downloading, "Stop it, delete it and find
 * another" is a checkbox ticked by default rather than a second button: the
 * safer, fuller action is what happens unless a person opts out of it.
 *
 * Self-contained: renders its own trigger button and confirm dialog, checks
 * the download-client telemetry for a live match when it opens, and reports
 * back through `onBlocked` so the caller can refresh whatever list it came
 * from. Every instance's accessible name includes the release name, so a
 * list of several releases never has two controls announced the same way.
 */
import { useEffect, useState } from "react";
import { RotateCcw, ShieldBan } from "lucide-react";
import { Button } from "../ui/button";
import { CheckboxRow } from "../ui/checkbox";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { toast } from "../shell/toaster";
import { describeRequestFailure } from "../../lib/search-reasons";
import {
  blockReleaseOnly,
  checkBlockedMatch,
  checkQueueMatch,
  liftBlock,
  stopBlockAndSearchAgain,
  type QueueMatch,
  type ReleaseIdentity
} from "../../lib/block-release";
import type { BlockedRelease } from "../../lib/api";

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
  // Ticked by default whenever a match is found (#925): the safer, fuller
  // action — stop it, delete its files, find another — is what happens
  // unless a person opts out of it.
  const [stopToo, setStopToo] = useState(true);
  const [busy, setBusy] = useState(false);

  // Whether this exact release is already refused, so the trigger reads
  // "Lift the block" instead of "Block this release" wherever blocking is
  // offered (#925), not only on the Blocklist screen.
  const [blockedMatch, setBlockedMatch] = useState<BlockedRelease | null>(null);

  const displayTitle = identity.title || identity.releaseName;
  const isBlocked = blockedMatch !== null;

  useEffect(() => {
    let cancelled = false;
    void checkBlockedMatch(identity).then((found) => {
      if (!cancelled) setBlockedMatch(found);
    });
    return () => {
      cancelled = true;
    };
    // Only the identity that decides the match should re-run the check.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [identity.releaseName, identity.hash]);

  async function handleOpen() {
    setOpen(true);
    if (isBlocked) return;
    setMatch(null);
    setStopToo(true);
    setChecking(true);
    try {
      setMatch(await checkQueueMatch(identity));
    } catch {
      // The check is a convenience before blocking; the dialog still opens and says so.
      toast.error("Deluno could not check whether this release is in the queue. You can still block it.");
    } finally {
      setChecking(false);
    }
  }

  async function handleLift() {
    if (!blockedMatch) return;
    setBusy(true);
    try {
      const response = await liftBlock(blockedMatch.id);
      if (!response.ok) throw new Error("lift-block-failed");
      // No search is started (DESIGN-007 decision 8): clearing several at
      // once would otherwise fire one search per row, and somebody lifting
      // a single block searches for it themselves.
      toast.success(`"${displayTitle}" can be used again. Search for the title when you want it.`);
      setOpen(false);
      setBlockedMatch(null);
      onBlocked?.();
    } catch (error) {
      const explained = await describeRequestFailure(null, error, { action: "lift this block" });
      toast.error(explained.title, { description: explained.description });
    } finally {
      setBusy(false);
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

  async function handleConfirm() {
    if (match && stopToo) {
      await handleStopAndBlock();
    } else {
      await handleJustBlock();
    }
  }

  const description = match
    ? `Deluno found this release downloading in ${match.clientName} right now. Blocking it refuses every indexer's copy from now on.`
    : `Refuses every indexer's copy of "${identity.releaseName}" from now on. Nothing already downloaded is touched.`;

  return (
    <>
      <Button
        type="button"
        variant={variant}
        size={size}
        className={className}
        aria-label={isBlocked ? `Lift the block on "${displayTitle}"` : `Block "${displayTitle}"`}
        onClick={() => void handleOpen()}
      >
        {isBlocked ? <RotateCcw className="h-3.5 w-3.5" /> : <ShieldBan className="h-3.5 w-3.5" />}
        {isBlocked ? "Lift the block" : "Block this release"}
      </Button>

      {isBlocked ? (
        <ConfirmDialog
          open={open}
          onOpenChange={(next) => {
            if (!busy) setOpen(next);
          }}
          title={`Lift the block on "${displayTitle}"?`}
          description="Deluno will consider this release again. Nothing is searched for automatically — search for the title yourself when you want it."
          confirmLabel="Lift the block"
          confirmVariant="default"
          busy={busy}
          onConfirm={() => void handleLift()}
        />
      ) : (
        <ConfirmDialog
          open={open}
          onOpenChange={(next) => {
            if (!busy) setOpen(next);
          }}
          title={`Block "${displayTitle}"?`}
          description={checking ? "Checking whether it's downloading right now…" : description}
          confirmLabel={match ? (stopToo ? "Stop it, delete its files and find another" : "Just block it") : "Block this release"}
          confirmVariant="destructive"
          busy={busy || checking}
          onConfirm={() => void handleConfirm()}
          extraContent={match ? (
            <CheckboxRow
              label="Stop it, delete it and find another"
              description={`Also removes it from ${match.clientName}, deletes its files, and starts a replacement search. Left unticked, only the block itself happens.`}
              checked={stopToo}
              onCheckedChange={setStopToo}
              disabled={busy}
            />
          ) : undefined}
        />
      )}
    </>
  );
}
