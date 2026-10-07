import { CheckCircle2, CircleAlert, Info, LoaderCircle } from "lucide-react";
import { cn } from "../../lib/utils";

/**
 * What the last "Search now" on this page did, kept on the page (#1157).
 *
 * A toast is gone in a few seconds and is easy to miss, so a search that
 * finished with "nothing found" looked like a button that did nothing. This
 * stays until the next search: searching, then what was sent, or why nothing was.
 */
export type SearchNote =
  | { state: "searching"; label: string }
  | { state: "done"; tone: "success" | "info" | "error" | "warning"; title: string; description?: string };

export function SearchResultNote({ note, className }: { note: SearchNote | null; className?: string }) {
  if (!note) return null;
  const searching = note.state === "searching";
  const tone = searching ? "info" : note.tone;
  const Icon = searching ? LoaderCircle : tone === "success" ? CheckCircle2 : tone === "info" ? Info : CircleAlert;
  return (
    <div
      role="status"
      aria-label="Result of the last search"
      className={cn(
        "flex items-start gap-2 rounded-xl border px-4 py-3 text-sm",
        tone === "success" && "border-success/30 bg-success/10",
        tone === "info" && "border-hairline bg-surface-1",
        tone === "warning" && "border-warning/30 bg-warning/10",
        tone === "error" && "border-destructive/30 bg-destructive/10",
        className
      )}
    >
      <Icon className={cn("mt-0.5 h-4 w-4 shrink-0", searching && "animate-spin")} aria-hidden />
      <div className="min-w-0">
        <p className="font-semibold text-foreground">{searching ? note.label : note.title}</p>
        {!searching && note.description ? <p className="mt-0.5 text-muted-foreground">{note.description}</p> : null}
      </div>
    </div>
  );
}
