/**
 * Migration Assistant — list → drawer over one page-level form.
 *
 *   PageToolbar (System settings tabs · Load example · Preview import)
 *   ListCard     import source (source, name, JSON; safety notes behind a Disclosure)
 *   SummaryStrip preview result (create · skip · unsupported · titles)
 *   ListCard     inventory (row reconciliation · actions · classifications · download)
 *   ListCard     change report  (row → drawer: what it is · fields · warnings)
 *   ListCard     imported connections (test each one, result lands in its row)
 *   ListCard     applied history (row reopens that record)
 *
 * Nothing here overwrites: a preview and an apply run the same mapping code,
 * and anything already present is reported as skipped rather than replaced.
 *
 * Contracts: POST /api/migration/preview, POST /api/migration/apply,
 * GET /api/migration/reports, POST /api/{indexers|download-clients}/{id}/test.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { ArrowRight, Download, Loader2, ShieldCheck } from "lucide-react";
import { Button } from "../components/ui/button";
import { Chip, type ChipProps } from "../components/ui/chip";
import { Disclosure } from "../components/ui/disclosure";
import { Drawer, DrawerFooter, DrawerSection } from "../components/ui/drawer";
import { Field, FieldRow } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { Select } from "../components/ui/select";
import { SummaryStrip } from "../components/ui/summary-strip";
import { Switch } from "../components/ui/switch";
import { Textarea } from "../components/ui/textarea";
import { systemSettingsNavItems } from "../components/app/settings-shell";
import {
  fetchJson,
  type MigrationApplyResponse,
  type MigrationAuditReport,
  type MigrationReport,
  type MigrationReportOperation
} from "../lib/api";
import { formatDateTime, formatShortDate, formatTime, useDisplayPreferences } from "../lib/display-preferences";

const SOURCE_OPTIONS = [
  { label: "Radarr", value: "radarr" },
  { label: "Sonarr", value: "sonarr" },
  { label: "Prowlarr", value: "prowlarr" },
  { label: "Recyclarr", value: "recyclarr" },
  { label: "Compatible JSON", value: "custom" }
];

const SAFETY_NOTES = [
  "A preview and an apply run the same mapping code, so what you see is what runs.",
  "Existing libraries, profiles, sources and clients are skipped, never overwritten.",
  "Missing host, URL or feed data is reported as unsupported rather than guessed.",
  "Monitored and wanted titles are reported for reconciliation, not imported blind."
];

const SAMPLE_PAYLOAD = `{
  "qualityProfiles": [
    {
      "name": "Imported 1080p",
      "cutoff": 2,
      "items": [
        { "allowed": true, "quality": { "id": 1, "name": "WEB 720p" } },
        { "allowed": true, "quality": { "id": 2, "name": "WEB 1080p" } }
      ]
    }
  ],
  "rootFolders": [
    { "path": "/data/media/movies" }
  ],
  "indexers": [
    { "name": "Existing Indexer", "protocol": "torrent", "baseUrl": "https://indexer.example/api", "categories": [2000, 2010] }
  ],
  "downloadClients": [
    { "name": "qBittorrent", "implementation": "QBittorrent", "host": "qbittorrent", "port": 8080 }
  ]
}`;

interface ConnectionValidationResult {
  healthStatus: string;
  message: string;
  failureCategory?: string | null;
  latencyMs?: number | null;
}

export function SettingsMigrationPage() {
  const { preferences } = useDisplayPreferences();
  const [sourceKind, setSourceKind] = useState("radarr");
  const [sourceName, setSourceName] = useState("Radarr");
  const [payloadJson, setPayloadJson] = useState("");
  const [allowAdvancedLegacyRules, setAllowAdvancedLegacyRules] = useState(false);
  const [safetyOpen, setSafetyOpen] = useState(false);

  const [report, setReport] = useState<MigrationReport | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [applied, setApplied] = useState<MigrationApplyResponse | null>(null);
  const [auditReports, setAuditReports] = useState<MigrationAuditReport[]>([]);
  const [busy, setBusy] = useState<"preview" | "apply" | "validate" | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [connectionValidation, setConnectionValidation] = useState<Record<string, ConnectionValidationResult>>({});
  const [openOperationId, setOpenOperationId] = useState<string | null>(null);

  const canApply = report?.valid === true && selectedIds.size > 0;
  const openOperation = openOperationId ? report?.operations.find((operation) => operation.id === openOperationId) ?? null : null;

  useEffect(() => {
    void fetchJson<MigrationAuditReport[]>("/api/migration/reports").then(setAuditReports).catch(() => setAuditReports([]));
  }, []);

  async function handlePreview(event?: FormEvent<HTMLFormElement>) {
    event?.preventDefault();
    setBusy("preview");
    setMessage(null);
    setApplied(null);
    setConnectionValidation({});
    try {
      const next = await fetchJson<MigrationReport>("/api/migration/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sourceKind, sourceName, payloadJson, allowAdvancedLegacyRules })
      });
      setReport(next);
      setSelectedIds(new Set(next.operations.filter(isSelectable).map((operation) => operation.id)));
      setMessage(next.valid ? "Preview ready. Review every create, skip and warning before applying." : "Preview found issues — see the change report.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Migration preview failed.");
    } finally {
      setBusy(null);
    }
  }

  async function handleApply() {
    setBusy("apply");
    setMessage(null);
    try {
      const result = await fetchJson<MigrationApplyResponse>("/api/migration/apply", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sourceKind, sourceName, payloadJson, selectedOperationIds: [...selectedIds], allowAdvancedLegacyRules })
      });
      setApplied(result);
      setReport(result.report);
      setConnectionValidation({});
      setAuditReports(await fetchJson<MigrationAuditReport[]>("/api/migration/reports").catch(() => []));
      const created = result.applied.filter((item) => item.result === "created").length;
      setMessage(
        result.report.errors.length > 0
          ? "Migration paused safely. Review the report, then retry — anything already saved is skipped."
          : `${created} selected ${created === 1 ? "item" : "items"} imported. Anything already present was skipped.`
      );
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Migration apply failed.");
    } finally {
      setBusy(null);
    }
  }

  const importedConnections = useMemo(
    () =>
      (applied?.applied ?? []).filter(
        (item) => item.result === "created" && item.createdId && (item.targetType === "indexer" || item.targetType === "download-client")
      ),
    [applied]
  );

  async function validateImportedConnections() {
    if (!importedConnections.length) return;
    setBusy("validate");
    const results: Record<string, ConnectionValidationResult> = {};
    await Promise.all(
      importedConnections.map(async (item) => {
        const collection = item.targetType === "indexer" ? "indexers" : "download-clients";
        try {
          results[item.operationId] = await fetchJson<ConnectionValidationResult>(`/api/${collection}/${item.createdId}/test`, { method: "POST" });
        } catch (error) {
          results[item.operationId] = { healthStatus: "needs review", message: error instanceof Error ? error.message : "The test could not run." };
        }
      })
    );
    setConnectionValidation(results);
    setBusy(null);
  }

  function toggleOperation(operation: MigrationReportOperation, on: boolean) {
    setSelectedIds((current) => {
      const next = new Set(current);
      if (on) next.add(operation.id);
      else next.delete(operation.id);
      return next;
    });
  }

  return (
    <form onSubmit={(event) => void handlePreview(event)} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar
        tabs={systemSettingsNavItems}
        actions={
          <>
            <Button type="button" variant="outline" onClick={() => setPayloadJson(SAMPLE_PAYLOAD)}>
              Load example
            </Button>
            <Button type="submit" disabled={busy !== null || !payloadJson.trim()}>
              {busy === "preview" ? <Loader2 className="h-4 w-4 animate-spin" /> : <ShieldCheck className="h-4 w-4" />}
              Preview import
            </Button>
          </>
        }
      />

      <ListCard title="Import source" count="Nothing existing is overwritten — a preview always comes first">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <FieldRow>
            <Field label="Exported from" help="The application this snapshot came out of.">
              <Select
                value={sourceKind}
                onChange={(event) => {
                  setSourceKind(event.target.value);
                  setSourceName(SOURCE_OPTIONS.find((option) => option.value === event.target.value)?.label ?? "External stack");
                }}
                options={SOURCE_OPTIONS.map((option) => ({ value: option.value, label: option.label }))}
              />
            </Field>
            <Field label="Call this import" help="Used to label this run in the history below.">
              <Input value={sourceName} onChange={(event) => setSourceName(event.target.value)} placeholder="Home Radarr" />
            </Field>
          </FieldRow>
          <Field label="Exported JSON" help="Paste the snapshot. Deluno maps it and shows you every change before anything is written.">
            <Textarea
              value={payloadJson}
              onChange={(event) => setPayloadJson(event.target.value)}
              spellCheck={false}
              placeholder={SAMPLE_PAYLOAD}
              className="min-h-[18rem] font-mono"
            />
          </Field>
          <Disclosure title="What migration will and will not do" summary="Four rules that make this safe to run twice" open={safetyOpen} onOpenChange={setSafetyOpen}>
            <ul className="grid gap-2">
              {SAFETY_NOTES.map((note) => (
                <li key={note} className="text-[length:var(--type-body-sm)] text-muted-foreground">
                  {note}
                </li>
              ))}
            </ul>
          </Disclosure>
          <div className="flex items-center justify-between gap-[var(--grid-gap)] rounded-[var(--radius-control)] border border-hairline bg-surface-muted px-3 py-2.5">
            <div className="min-w-0">
              <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">Keep opaque rules as Advanced legacy input</p>
              <p className="text-[length:var(--type-caption)] text-muted-foreground">Stores their exact matcher for review/export; it does not add legacy numbers to typed decisions.</p>
            </div>
            <Switch
              aria-label="Keep opaque rules as Advanced legacy input"
              checked={allowAdvancedLegacyRules}
              onCheckedChange={setAllowAdvancedLegacyRules}
            />
          </div>
        </div>
      </ListCard>

      {report ? (
        <SummaryStrip
          cells={[
            { label: "To create", value: String(report.summary.createCount), help: "new in Deluno", tone: report.summary.createCount ? "success" : undefined },
            { label: "Already here", value: String(report.summary.skipCount), help: "skipped, not touched" },
            { label: "Unsupported", value: String(report.summary.unsupportedCount), help: "reported, not guessed", tone: report.summary.unsupportedCount ? "warning" : undefined },
            { label: "Titles seen", value: String(report.summary.titleCount), help: "for reconciliation" },
            { label: "Selected", value: String(selectedIds.size), help: message ?? "ready to apply" }
          ]}
        />
      ) : null}

      {report?.inventory && report.inventory.entries.length ? (
        <ListCard
          title="Import inventory"
          count={`${report.inventory.accountedRowCount} of ${report.inventory.inputRowCount} legacy rows accounted for`}
          actions={
            <Chip tone={report.inventory.unaccountedRowCount ? "warn" : "ok"}>
              {report.inventory.unaccountedRowCount ? `${report.inventory.unaccountedRowCount} need review` : "Complete"}
            </Chip>
          }
        >
          <ListTable columns={[{ label: "Source" }, { label: "Rows", width: "120px" }, { label: "Actions", width: "minmax(0,1.6fr)" }, { label: "Classification", width: "minmax(0,1.6fr)" }]}>
            {report.inventory.entries.map((entry) => (
              <ListRow key={`${entry.sourceKind}-${entry.category}-${entry.mediaType}`}>
                <ListNameCell name={entry.category} sub={`${entry.sourceKind} · ${entry.mediaType}`} />
                <ListCell numeric primary={`${entry.accountedRowCount}/${entry.inputRowCount}`} secondary={entry.unaccountedRowCount ? `${entry.unaccountedRowCount} unaccounted` : "All rows mapped"} />
                <ListCell primary={formatCounts(entry.actionCounts, "No mapped actions")} />
                <ListCell primary={formatCounts(entry.classificationCounts, "No special classification")} secondary={entry.warnings.length ? entry.warnings.join(" ") : undefined} />
              </ListRow>
            ))}
          </ListTable>
        </ListCard>
      ) : null}

      {report ? (
        <ListCard
          title="Change report"
          count={`${report.sourceName} · ${report.sourceKind}`}
          actions={
            <div className="flex flex-wrap items-center gap-2">
              <Button type="button" variant="outline" onClick={() => downloadMigrationReport(report)}>
                <Download className="h-4 w-4" />
                Download report
              </Button>
              <Button type="button" onClick={() => void handleApply()} disabled={!canApply || busy !== null}>
                {busy === "apply" ? <Loader2 className="h-4 w-4 animate-spin" /> : <ArrowRight className="h-4 w-4" />}
                Apply {selectedIds.size} selected
              </Button>
            </div>
          }
        >
          {report.operations.length === 0 ? (
            <ListEmpty title="Nothing to import" description="No configuration Deluno recognises was found in this payload. Check that you exported the whole snapshot." />
          ) : (
            <ListTable
              columns={[
                { label: "Item" },
                { label: "Why", width: "minmax(0,1.8fr)" },
                { label: "Action", width: LIST_TRACK.status, mobile: true },
                { label: "Apply", width: LIST_TRACK.toggle, mobile: true }
              ]}
            >
              {report.operations.map((operation) => (
                <ListRow key={operation.id} onClick={() => setOpenOperationId(operation.id)} selected={openOperationId === operation.id}>
                  <ListNameCell name={operation.name} sub={operation.targetType} />
                  <ListCell primary={operation.reason} secondary={operation.warnings.length ? `${operation.warnings.length} ${operation.warnings.length === 1 ? "warning" : "warnings"}` : undefined} />
                  <ListCell mobile>
                    <Chip tone={actionTone(operation.action)}>{operation.action}</Chip>
                  </ListCell>
                  <ListCell mobile>
                    {isSelectable(operation) ? (
                      <Switch
                        size="sm"
                        aria-label={`Apply ${operation.name}`}
                        checked={selectedIds.has(operation.id)}
                        onCheckedChange={(checked) => toggleOperation(operation, checked)}
                      />
                    ) : (
                      <span className="text-[length:var(--type-caption)] text-muted-foreground">—</span>
                    )}
                  </ListCell>
                </ListRow>
              ))}
            </ListTable>
          )}
        </ListCard>
      ) : null}

      {importedConnections.length ? (
        <ListCard
          title="Imported connections"
          count="Test what came across before you rely on it"
          actions={
            <Button type="button" variant="outline" size="sm" onClick={() => void validateImportedConnections()} disabled={busy !== null}>
              {busy === "validate" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <ShieldCheck className="h-3.5 w-3.5" />}
              Test all
            </Button>
          }
        >
          <ListTable columns={[{ label: "Connection" }, { label: "Result", width: "minmax(0,1.8fr)" }, { label: "Status", width: LIST_TRACK.status, mobile: true }]} chevron={false}>
            {importedConnections.map((item) => {
              const result = connectionValidation[item.operationId];
              return (
                <ListRow key={item.operationId}>
                  <ListNameCell name={item.name} sub={item.targetType === "indexer" ? "Indexer" : "Download client"} />
                  <ListCell primary={result?.message ?? "Not tested yet"} secondary={result?.latencyMs != null ? `${result.latencyMs} ms` : undefined} />
                  <ListCell mobile>
                    <Chip tone={result ? (result.healthStatus === "healthy" ? "ok" : "warn") : "idle"}>{result?.healthStatus ?? "Untested"}</Chip>
                  </ListCell>
                </ListRow>
              );
            })}
          </ListTable>
        </ListCard>
      ) : null}

      {auditReports.length ? (
        <ListCard title="Applied history" count="Redacted records of real imports — previews are never stored">
          <ListTable columns={[{ label: "Import" }, { label: "Outcome", width: "minmax(0,1.4fr)" }, { label: "When", width: "160px", mobile: true }]}>
            {auditReports.map((audit) => {
              const created = audit.applied.filter((item) => item.result === "created").length;
              const failed = audit.applied.filter((item) => item.result === "failed").length;
              return (
                <ListRow
                  key={audit.id}
                  onClick={() => {
                    setReport(audit.preflightReport);
                    setApplied({ report: audit.resultReport, applied: audit.applied, auditReportId: audit.id });
                    setSelectedIds(new Set());
                    setMessage(`Showing the record from ${formatDateTime(audit.appliedUtc, preferences)}.`);
                  }}
                >
                  <ListNameCell name={audit.sourceName} sub={audit.preflightReport.sourceKind} />
                  <ListCell primary={`${created} created`} secondary={failed ? `${failed} failed` : "no failures"} />
                  <ListCell numeric mobile primary={formatShortDate(audit.appliedUtc, { ...preferences, showRelativeDates: false })} secondary={formatTime(audit.appliedUtc, preferences)} />
                </ListRow>
              );
            })}
          </ListTable>
        </ListCard>
      ) : null}

      <Drawer
        open={openOperation !== null}
        onOpenChange={(open) => {
          if (!open) setOpenOperationId(null);
        }}
        title={openOperation?.name ?? "Change"}
        description={openOperation ? `${openOperation.action} · ${openOperation.targetType}` : undefined}
        footer={<DrawerFooter state="clean" saveType="button" saveLabel="Close" saveEnabled={false} onCancel={() => setOpenOperationId(null)} />}
      >
        {openOperation ? (
          <>
            <DrawerSection title="Why">
              <p className="text-[length:var(--type-body-sm)] text-muted-foreground">{openOperation.reason}</p>
              {isSelectable(openOperation) ? (
                <div className="flex items-center justify-between gap-[var(--grid-gap)]">
                  <span className="text-[length:var(--type-body-sm)] text-foreground">Apply this change</span>
                  <Switch
                    aria-label={`Apply ${openOperation.name}`}
                    checked={selectedIds.has(openOperation.id)}
                    onCheckedChange={(checked) => toggleOperation(openOperation, checked)}
                  />
                </div>
              ) : (
                <p className="text-[length:var(--type-caption)] text-muted-foreground">This one is review-only — Deluno will not write it.</p>
              )}
            </DrawerSection>

            {Object.entries(openOperation.data).filter(([, value]) => value).length ? (
              <DrawerSection title="What would be created">
                <div className="grid gap-1.5">
                  {Object.entries(openOperation.data)
                    .filter(([, value]) => value)
                    .map(([key, value]) => (
                      <div key={key} className="flex items-baseline justify-between gap-3 border-b border-hairline py-1.5 last:border-b-0">
                        <span className="shrink-0 text-[length:var(--type-caption)] text-muted-foreground">{key}</span>
                        <span className="min-w-0 truncate text-right font-mono text-[length:var(--type-caption)] text-foreground" title={value ?? undefined}>
                          {value}
                        </span>
                      </div>
                    ))}
                </div>
              </DrawerSection>
            ) : null}

            {openOperation.warnings.length ? (
              <DrawerSection title="Warnings">
                <div className="grid gap-1.5">
                  {openOperation.warnings.map((warning) => (
                    <p key={warning} className="text-[length:var(--type-body-sm)] text-warning">
                      {warning}
                    </p>
                  ))}
                </div>
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>
    </form>
  );
}

/* ---------------------------------------------------------------- bits */

/** Catalog monitored-state rows are applyable even though they create nothing. */
function isSelectable(operation: MigrationReportOperation) {
  return operation.canApply || (operation.category === "catalog" && operation.targetType === "monitored-state");
}

function actionTone(action: string): NonNullable<ChipProps["tone"]> {
  if (action === "create") return "ok";
  if (action === "unsupported" || action === "conflict") return "warn";
  if (action === "report") return "info";
  return "idle";
}

function formatCounts(counts: Record<string, number>, emptyLabel: string) {
  const values = Object.entries(counts);
  return values.length ? values.map(([label, count]) => `${label} ${count}`).join(" · ") : emptyLabel;
}

function downloadMigrationReport(report: MigrationReport) {
  const safeName = report.sourceName.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "import";
  const blob = new Blob([JSON.stringify(report, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `deluno-migration-${safeName}.json`;
  anchor.click();
  URL.revokeObjectURL(url);
}
