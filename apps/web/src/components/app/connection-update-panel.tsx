import { useState } from "react";
import { ExternalLink, Loader2 } from "lucide-react";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { DrawerSection } from "../ui/drawer";
import { fetchJson } from "../../lib/api";
import { queryClient } from "../../lib/route-cache";
import { dismissPath, updateNoticeTitle, useConnectionUpdates, type ConnectionUpdate, type ConnectionUpdateResult } from "../../lib/connection-updates";

/**
 * What a connection says about its newer version, in its own drawer (#1118): the release notes, and then what
 * Deluno can do. Update now only where Deluno installed the app on this computer and has a build it checked
 * (Weir's latest release, or the pinned qBittorrent or SABnzbd); anything else is a one-line how-to. Dismiss this
 * version hides the notice until an even newer version is out. Nothing at all when the connection is up to date.
 */
export function ConnectionUpdatePanel({ kind, id }: { kind: string; id: string }) {
  const item = useConnectionUpdates().find((entry) => entry.kind === kind && entry.id === id);
  // What Update now said stays on screen after the check clears the notice, so the result is never lost.
  const [result, setResult] = useState<ConnectionUpdateResult | null>(null);
  if (item) return <UpdateBody item={item} result={result} setResult={setResult} />;
  if (!result) return null;
  return (
    <DrawerSection title="New version">
      <ResultLine result={result} />
    </DrawerSection>
  );
}

function ResultLine({ result }: { result: ConnectionUpdateResult }) {
  return (
    <p role="status" className={result.outcome === "failed" ? "text-(length:--type-caption) text-destructive" : "text-(length:--type-caption) text-success"}>
      {result.message}
    </p>
  );
}

function UpdateBody({ item, result, setResult }: { item: ConnectionUpdate; result: ConnectionUpdateResult | null; setResult: (result: ConnectionUpdateResult) => void }) {
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState<"update" | "dismiss" | null>(null);

  async function updateNow() {
    setBusy("update");
    try {
      const outcome = await fetchJson<ConnectionUpdateResult>(`/api/connections/${encodeURIComponent(item.kind)}/${encodeURIComponent(item.id)}/update`, { method: "POST" });
      setResult(outcome);
      if (outcome.outcome === "failed") toast.error(outcome.message);
      else toast.success(outcome.message);
      await queryClient.invalidateQueries();
    } catch (error) {
      const message = `Failed: ${error instanceof Error ? error.message : "Deluno could not update it."}`;
      setResult({ outcome: "failed", message, version: null });
      toast.error(message);
    } finally {
      setBusy(null);
      setConfirm(false);
    }
  }

  async function dismiss() {
    setBusy("dismiss");
    try {
      await fetchJson(dismissPath(item.kind, item.id), { method: "POST" });
      toast.success(`Dismissed ${item.appName} ${item.latestVersion}`);
      await queryClient.invalidateQueries();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "That could not be dismissed.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <DrawerSection title="New version">
      <div className="space-y-2.5" data-testid="connection-update-panel">
        <p className="text-(length:--type-body-sm) text-foreground">{updateNoticeTitle(item)}</p>
        <p className="text-(length:--type-caption) text-muted-foreground">{item.message}</p>
        {result ? <ResultLine result={result} /> : null}
        <div className="flex flex-wrap items-center gap-2">
          {item.releaseUrl ? (
            <Button asChild variant="outline" size="sm">
              <a href={item.releaseUrl} target="_blank" rel="noreferrer noopener">
                Release notes <ExternalLink className="h-3.5 w-3.5" aria-hidden />
              </a>
            </Button>
          ) : null}
          {item.mode === "updateNow" ? (
            <Button type="button" size="sm" disabled={busy !== null} onClick={() => setConfirm(true)}>
              {busy === "update" ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden /> : null}
              Update now
            </Button>
          ) : null}
          {item.notice ? (
            <Button type="button" variant="ghost" size="sm" disabled={busy !== null} onClick={() => void dismiss()}>
              Dismiss this version
            </Button>
          ) : null}
        </div>
        {!item.notice ? (
          <p className="text-(length:--type-caption) text-muted-foreground">
            You dismissed {item.dismissedVersion ?? item.latestVersion}. The notice comes back only for a newer version.
          </p>
        ) : null}
      </div>
      <ConfirmDialog
        open={confirm}
        onOpenChange={(open) => {
          if (busy === null) setConfirm(open);
        }}
        title={`Update ${item.appName} now?`}
        description={`Deluno will close ${item.appName}, install ${item.targetVersion ?? item.latestVersion} and start it again. Its downloads pause until it is back, and Windows may ask for permission.`}
        confirmLabel="Update now"
        confirmVariant="default"
        busy={busy === "update"}
        onConfirm={() => void updateNow()}
      />
    </DrawerSection>
  );
}
