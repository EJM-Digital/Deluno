/**
 * What kind of thing an activity event is (#1096): one icon and one colour per
 * kind, so the stream and the hourly bars read the same way. The kind is worked
 * out from the event's category, the same codes the server's own counts use
 * (searches, grabs, imports, clean-ups, retention).
 */
import { Activity, AlertTriangle, CheckCircle2, Captions, Download, ListPlus, Search, Timer, Trash2, type LucideIcon } from "lucide-react";

export type ActivityKind = "search" | "grab" | "import" | "failed" | "cleanup" | "retention" | "subtitle" | "lists" | "other";

export interface KindStyle {
  label: string;
  icon: LucideIcon;
  /** Solid fill for a bar or dot. */
  fill: string;
  /** Text colour for the icon. */
  text: string;
  /** Tinted tile behind the icon. */
  tile: string;
}

export const KIND_STYLE: Record<ActivityKind, KindStyle> = {
  search: { label: "Searches", icon: Search, fill: "bg-[hsl(var(--stage-search))]", text: "text-[hsl(var(--stage-search))]", tile: "bg-[hsl(var(--stage-search)/0.14)]" },
  grab: { label: "Grabs", icon: Download, fill: "bg-[hsl(var(--stage-grab))]", text: "text-[hsl(var(--stage-grab))]", tile: "bg-[hsl(var(--stage-grab)/0.14)]" },
  import: { label: "Imports", icon: CheckCircle2, fill: "bg-success", text: "text-success", tile: "bg-success/15" },
  failed: { label: "Problems", icon: AlertTriangle, fill: "bg-destructive", text: "text-destructive", tile: "bg-destructive/15" },
  cleanup: { label: "Clean-ups", icon: Trash2, fill: "bg-[hsl(var(--stage-process))]", text: "text-[hsl(var(--stage-process))]", tile: "bg-[hsl(var(--stage-process)/0.14)]" },
  retention: { label: "Retention", icon: Timer, fill: "bg-warning", text: "text-warning", tile: "bg-warning/15" },
  subtitle: { label: "Subtitles", icon: Captions, fill: "bg-info", text: "text-info", tile: "bg-info/15" },
  lists: { label: "Lists and requests", icon: ListPlus, fill: "bg-primary", text: "text-primary", tile: "bg-primary/15" },
  other: { label: "Other", icon: Activity, fill: "bg-muted-foreground/60", text: "text-muted-foreground", tile: "bg-surface-3" }
};

/** The order kinds are stacked and listed in. */
export const KIND_ORDER: ActivityKind[] = ["search", "grab", "import", "cleanup", "retention", "subtitle", "lists", "failed", "other"];

/** Worked out from the event's category (and severity, for a problem). */
export function activityKind(category: string, severity?: string | null): ActivityKind {
  const code = (category ?? "").toLowerCase();
  if (severity === "error" || /(^|\.)(failed|failure|error)(\.|$)|\.problem$/.test(code)) return "failed";
  if (code.startsWith("retention")) return "retention";
  if (/\.search\.|^search|^rss/.test(code)) return "search";
  if (/release\.(force-)?grabbed|dispatch|^download\.(sent|grab)/.test(code)) return "grab";
  if (/import\.(season-pack\.)?completed|^filesystem\.import|^import/.test(code)) return "import";
  if (/orphan|queue\.removed|sharing\.(reclaimed|stopped)|cleanup|recycle/.test(code)) return "cleanup";
  if (code.startsWith("subtitle")) return "subtitle";
  if (/^(intake|trakt|mdblist|seerr|list|request)/.test(code)) return "lists";
  return "other";
}
