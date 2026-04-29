import { Badge } from "../ui/badge";
import type { DecisionExplanationItem } from "../../lib/api";

export function DecisionExplanationList({ decisions }: { decisions: DecisionExplanationItem[] }) {
  if (decisions.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">
        No recorded decisions yet. Searches, grabs, imports, and retries will appear here with their inputs and outcome.
      </p>
    );
  }

  return (
    <div className="space-y-3">
      {decisions.slice(0, 6).map((item) => (
        <article key={item.id} className="rounded-xl border border-hairline bg-surface-1 p-4">
          <div className="flex flex-wrap items-center gap-2">
            <Badge>{item.scope}</Badge>
            <Badge variant={statusVariant(item.status)}>{item.status}</Badge>
          </div>
          <p className="mt-3 text-sm font-medium leading-relaxed text-foreground">{item.reason}</p>
          <p className="mt-2 text-xs leading-relaxed text-muted-foreground">{item.outcome}</p>
          {item.alternatives.length ? (
            <div className="mt-3 space-y-2 border-t border-hairline pt-3">
              {item.alternatives.slice(0, 3).map((alternative) => (
                <div key={`${item.id}-${alternative.name}`} className="flex items-start justify-between gap-3 text-xs">
                  <div>
                    <p className="font-medium text-foreground">{alternative.name}</p>
                    <p className="mt-0.5 text-muted-foreground">{alternative.reason}</p>
                  </div>
                  <span className="tabular shrink-0 text-muted-foreground">
                    {alternative.score === null ? alternative.status : `${alternative.status} ${alternative.score}`}
                  </span>
                </div>
              ))}
            </div>
          ) : null}
        </article>
      ))}
    </div>
  );
}

function statusVariant(status: string): "success" | "warning" | "destructive" | "info" {
  const normalized = status.toLowerCase();
  if (["completed", "matched", "sent", "requeued"].includes(normalized)) return "success";
  if (["held", "checked", "planned", "started"].includes(normalized)) return "warning";
  if (["failed", "dead-letter", "blocked"].includes(normalized)) return "destructive";
  return "info";
}
