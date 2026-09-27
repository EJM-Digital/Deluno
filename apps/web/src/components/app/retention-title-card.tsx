import { useCallback, useEffect, useState } from "react";
import { fetchJson } from "../../lib/api";
import { useDisplayPreferences, formatDateTime } from "../../lib/display-preferences";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { Chip } from "../ui/chip";
import { LIST_TRACK, ListCard, ListCell, ListNameCell, ListRow, ListTable } from "../ui/list-card";
import { RetentionKeepDialog, type KeepChoice } from "./retention-keep-dialog";
import { retentionMarkPresentation, STATUS_PRESENTATION } from "../../lib/status-tones";

/** Mirrors `Deluno.Retention.RetentionTitleStatus`. */
interface RetentionTitleStatus {
  marks: {
    ruleId: string;
    ruleName: string;
    entityId: string;
    level: string;
    title: string;
    reasons: string[];
    removeAfterUtc: string;
    state: "leaving" | "ready" | "held" | string;
    stateReason: string | null;
  }[];
  keeps: { id: string; entityId: string; title: string; ruleId: string | null; ruleName: string | null; note: string | null; createdUtc: string }[];
  episodeRemovals: { episodeId: string; seasonNumber: number | null; episodeNumber: number | null; ruleName: string; reason: string; removedUtc: string }[];
}

type Mark = RetentionTitleStatus["marks"][number];

function episodeLabel(season: number | null, episode: number | null) {
  return `S${String(season ?? 0).padStart(2, "0")}E${String(episode ?? 0).padStart(2, "0")}`;
}

/**
 * What retention says about this title (#494, DESIGN-008): Leaving soon and
 * why, with Keep; kept, with the note and Undo; and episodes a rule removed,
 * with Undo. Nothing is drawn when retention has nothing to say.
 */
export function RetentionTitleCard({ entityId, onChanged }: { entityId: string; onChanged?: () => void }) {
  const { preferences } = useDisplayPreferences();
  const [status, setStatus] = useState<RetentionTitleStatus | null>(null);
  const [keeping, setKeeping] = useState<Mark | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    setStatus(await fetchJson<RetentionTitleStatus>(`/api/retention/titles/${encodeURIComponent(entityId)}`).catch(() => null));
  }, [entityId]);

  useEffect(() => {
    void load();
  }, [load]);

  if (!status || (status.marks.length === 0 && status.keeps.length === 0 && status.episodeRemovals.length === 0)) {
    return null;
  }

  async function keep(mark: Mark, choice: KeepChoice) {
    setBusy("keep");
    try {
      const response = await authedFetch("/api/retention/keeps", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ entityId: mark.entityId, ruleId: choice.scope === "rule" ? mark.ruleId : null, note: choice.note })
      });
      toast[response.ok ? "success" : "error"](response.ok ? `${mark.title} is kept.` : `${mark.title} could not be kept.`);
      setKeeping(null);
      await load();
      onChanged?.();
    } finally {
      setBusy(null);
    }
  }

  async function undo(path: string, key: string, done: string) {
    setBusy(key);
    try {
      const response = await authedFetch(path, { method: "DELETE" });
      toast[response.ok ? "success" : "error"](response.ok ? done : "That could not be undone.");
      await load();
      onChanged?.();
    } finally {
      setBusy(null);
    }
  }

  const rows = status.marks.length + status.keeps.length + status.episodeRemovals.length;
  return (
    <>
      <ListCard title="Retention" count={`${rows} ${rows === 1 ? "item" : "items"}`}>
        <ListTable chevron={false} columns={[{ label: "Rule" }, { label: "Why" }, { label: "When" }, { label: "", width: LIST_TRACK.action, align: "end", mobile: true, srOnly: true }]}>
          {status.marks.map((mark) => (
            <ListRow key={`${mark.ruleId}:${mark.entityId}`}>
              <ListNameCell name={`“${mark.ruleName}”`} sub={mark.level === "movie" || mark.level === "show" ? undefined : mark.title} />
              <ListCell primary={mark.reasons[0] ?? "—"} secondary={mark.reasons.length > 1 ? `and ${mark.reasons.length - 1} more` : undefined} />
              <ListCell
                primary={<Chip tone={retentionMarkPresentation(mark.state).tone}>{retentionMarkPresentation(mark.state).label}</Chip>}
                secondary={mark.state === "held" ? mark.stateReason ?? undefined : formatDateTime(mark.removeAfterUtc, preferences)}
              />
              <div role="cell" className="flex justify-end">
                <Button type="button" size="sm" variant="outline" onClick={() => setKeeping(mark)} disabled={busy !== null}>Keep…</Button>
              </div>
            </ListRow>
          ))}
          {status.keeps.map((item) => (
            <ListRow key={item.id}>
              <ListNameCell name={item.ruleId ? `“${item.ruleName ?? "Deleted rule"}”` : "Every rule"} />
              <ListCell primary={item.note ?? <span className="text-muted-foreground">—</span>} />
              <ListCell primary={<Chip tone={STATUS_PRESENTATION["retention.kept"].tone}>{STATUS_PRESENTATION["retention.kept"].label}</Chip>} secondary={formatDateTime(item.createdUtc, preferences)} />
              <div role="cell" className="flex justify-end">
                <Button type="button" size="sm" variant="ghost" onClick={() => void undo(`/api/retention/keeps/${item.id}`, `keep:${item.id}`, `${item.title} is no longer kept.`)} disabled={busy !== null}>Undo</Button>
              </div>
            </ListRow>
          ))}
          {status.episodeRemovals.map((item) => (
            <ListRow key={item.episodeId}>
              <ListNameCell name={`“${item.ruleName}”`} sub={episodeLabel(item.seasonNumber, item.episodeNumber)} />
              <ListCell primary={item.reason} />
              <ListCell primary={<Chip tone={STATUS_PRESENTATION["retention.removed"].tone}>{STATUS_PRESENTATION["retention.removed"].label}</Chip>} secondary={formatDateTime(item.removedUtc, preferences)} />
              <div role="cell" className="flex justify-end">
                <Button
                  type="button"
                  size="sm"
                  variant="ghost"
                  onClick={() => void undo(`/api/retention/episode-removals/${item.episodeId}`, `episode:${item.episodeId}`, `${episodeLabel(item.seasonNumber, item.episodeNumber)} is wanted again.`)}
                  disabled={busy !== null}
                >
                  Want it again
                </Button>
              </div>
            </ListRow>
          ))}
        </ListTable>
      </ListCard>

      <RetentionKeepDialog
        open={keeping !== null}
        onOpenChange={(next) => {
          if (!next) setKeeping(null);
        }}
        title={keeping?.title ?? "title"}
        ruleName={keeping?.ruleName ?? "this rule"}
        busy={busy === "keep"}
        onConfirm={(choice) => keeping && void keep(keeping, choice)}
      />
    </>
  );
}
