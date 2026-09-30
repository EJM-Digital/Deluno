import { useEffect, useState } from "react";
import { CheckCircle2 } from "lucide-react";
import { Button } from "../ui/button";
import { fetchJson, type BundledComponentConnectResult, type BundledComponentStatus } from "../../lib/api";
import { ConnectInstalledApps } from "./connect-installed-apps";

export type { BundledComponentStatus };

/**
 * What the installer's component picker put on this machine, or found already
 * there (#640, #1061).
 *
 * Installed apps are listed by "Connect the apps you installed": Deluno
 * connects nothing by itself, so each gets a Connect button. An app that is
 * only reachable (running, but not put here by the installer) still gets a
 * "Use" button that opens the form with host and port filled in.
 *
 * Nothing shows once every detected app of this category is connected or
 * already has a saved connection: the point is to remove typing, not to nag.
 */
export function DetectedComponents({
  category,
  configuredKinds,
  onUse,
  onNeedsSignIn
}: {
  category: "download-client" | "processor";
  /** Protocol keys (download clients) or lowercased names (processors) already saved. */
  configuredKinds: string[];
  onUse: (status: BundledComponentStatus) => void;
  /** An installed app Deluno could not sign in to: open the form with host and port filled in. Falls back to `onUse`. */
  onNeedsSignIn?: (status: BundledComponentStatus, result: BundledComponentConnectResult) => void;
}) {
  const [items, setItems] = useState<BundledComponentStatus[] | null>(null);

  useEffect(() => {
    let active = true;
    void fetchJson<BundledComponentStatus[]>("/api/system/bundled-components")
      .then((next) => { if (active) setItems(next); })
      .catch(() => { if (active) setItems(null); });
    return () => { active = false; };
  }, []);

  const reachableOnly = (items ?? []).filter((item) =>
    item.category === category &&
    !item.installed &&
    item.reachable &&
    !configuredKinds.includes(item.kind));

  return (
    <>
      <ConnectInstalledApps
        category={category}
        hideConnected
        onNeedsSignIn={(app, result) => (onNeedsSignIn ? onNeedsSignIn(app, result) : onUse(app))}
      />
      {reachableOnly.length > 0 ? (
        <div className="mb-3 flex flex-wrap items-center gap-2 rounded-md border border-dashed border-border/70 bg-muted/30 px-3 py-2 text-sm" role="status">
          <CheckCircle2 className="h-4 w-4 shrink-0 text-success" aria-hidden />
          <span className="text-muted-foreground">Detected on this machine:</span>
          {reachableOnly.map((item) => (
            <Button key={item.kind} type="button" size="sm" variant="outline" onClick={() => onUse(item)}>
              Use {item.displayName} ({item.defaultHost}:{item.defaultPort})
            </Button>
          ))}
        </div>
      ) : null}
    </>
  );
}
