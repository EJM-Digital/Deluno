import type { AttentionSummary, AttentionSummaryRow } from "./attention-summary";

/**
 * Test support: rows as the server builds them (`AttentionSummaryBuilder`, #1185). The rows carry the words, so a
 * test of the bell is a test of drawing what the server said, never of working anything out.
 */
export function serverRow(over: Partial<AttentionSummaryRow> & Pick<AttentionSummaryRow, "id" | "kind">): AttentionSummaryRow {
  return {
    section: "deluno",
    tone: "warn",
    title: over.id,
    text: "Something needs a look.",
    to: "/system",
    action: "Open System",
    count: 1,
    sinceUtc: null,
    data: null,
    ...over
  };
}

export function stuckRow(id: string, hours: number, over: Partial<AttentionSummaryRow> = {}, now = Date.parse("2026-10-01T09:00:00Z")): AttentionSummaryRow {
  const sinceUtc = new Date(now - hours * 3_600_000).toISOString();
  return serverRow({
    id: `stuck:qb:${id}`,
    kind: "stuck",
    section: "media",
    tone: "bad",
    title: `Show ${id}`,
    text: "Not moving for 2 days.",
    to: "/queue",
    action: "Open Transfers",
    sinceUtc,
    data: { id, clientId: "qb", clientName: "qBittorrent", title: `Show ${id}`, releaseName: `Show.${id}.1080p`, status: "stalled", errorMessage: null, healthFindings: [{ kind: "stalled", summary: "Not moving for 2 days." }], addedUtc: sinceUtc },
    ...over
  });
}

/** A summary whose count is the rows' counts added up, as the server's is. Notices are not counted. */
export function summaryOf(rows: AttentionSummaryRow[], notices: AttentionSummaryRow[] = [], over: Partial<AttentionSummary> = {}): AttentionSummary {
  return { count: rows.reduce((sum, row) => sum + row.count, 0), rows, notices, leftovers: 0, suggestions: 0, ...over };
}
