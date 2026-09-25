import { useEffect, useState } from "react";
import { CheckCircle2 } from "lucide-react";
import { Button } from "../ui/button";
import { fetchJson } from "../../lib/api";

export type BundledComponentStatus = {
  kind: string;
  displayName: string;
  category: "download-client" | "processor" | string;
  installed: boolean;
  reachable: boolean;
  defaultHost: string;
  defaultPort: number;
};

/**
 * What the installer's component picker put on this machine, or found already
 * there (#640). Shown above the "New client"/"Connect processor" actions so
 * the person never has to type a host or port for something Deluno can see
 * for itself — installed (its own uninstall entry exists) or reachable (its
 * default port answers).
 *
 * Nothing shows once every detected component of this category already has a
 * matching saved connection: the whole point is to remove typing, not to keep
 * nagging about a component that is already configured.
 */
export function DetectedComponents({
  category,
  configuredKinds,
  onUse
}: {
  category: "download-client" | "processor";
  /** Protocol keys (download clients) or lowercased names (processors) already saved. */
  configuredKinds: string[];
  onUse: (status: BundledComponentStatus) => void;
}) {
  const [items, setItems] = useState<BundledComponentStatus[] | null>(null);

  useEffect(() => {
    let active = true;
    void fetchJson<BundledComponentStatus[]>("/api/system/bundled-components")
      .then((next) => { if (active) setItems(next); })
      .catch(() => { if (active) setItems(null); });
    return () => { active = false; };
  }, []);

  const detected = (items ?? []).filter((item) =>
    item.category === category &&
    (item.installed || item.reachable) &&
    !configuredKinds.includes(item.kind));

  if (detected.length === 0) {
    return null;
  }

  return (
    <div className="mb-3 flex flex-wrap items-center gap-2 rounded-md border border-dashed border-border/70 bg-muted/30 px-3 py-2 text-sm" role="status">
      <CheckCircle2 className="h-4 w-4 shrink-0 text-success" aria-hidden />
      <span className="text-muted-foreground">Detected on this machine:</span>
      {detected.map((item) => (
        <Button key={item.kind} type="button" size="sm" variant="outline" onClick={() => onUse(item)}>
          Use {item.displayName} ({item.defaultHost}:{item.defaultPort})
        </Button>
      ))}
    </div>
  );
}
