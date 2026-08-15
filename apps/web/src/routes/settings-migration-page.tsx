import { useEffect, useMemo, useState, type FormEvent } from "react";
import { ArrowRight, CheckCircle2, FileJson, LoaderCircle, ShieldCheck, TriangleAlert } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Input } from "../components/ui/input";
import { InputDescription } from "../components/ui/input-description";
import { fetchJson, type MigrationApplyResponse, type MigrationAuditReport, type MigrationReport, type MigrationReportOperation } from "../lib/api";

const SOURCE_OPTIONS = [
  { label: "Radarr", value: "radarr" },
  { label: "Sonarr", value: "sonarr" },
  { label: "Prowlarr", value: "prowlarr" },
  { label: "Recyclarr", value: "recyclarr" },
  { label: "Compatible JSON", value: "custom" }
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
  const [sourceKind, setSourceKind] = useState("radarr");
  const [sourceName, setSourceName] = useState("Radarr");
  const [payloadJson, setPayloadJson] = useState("");
  const [report, setReport] = useState<MigrationReport | null>(null);
  const [selectedOperationIds, setSelectedOperationIds] = useState<Set<string>>(new Set());
  const [applied, setApplied] = useState<MigrationApplyResponse | null>(null);
  const [auditReports, setAuditReports] = useState<MigrationAuditReport[]>([]);
  const [busy, setBusy] = useState<"preview" | "apply" | "validate" | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [connectionValidation, setConnectionValidation] = useState<Record<string, ConnectionValidationResult>>({});

  const canApply = useMemo(
    () => report?.valid === true && selectedOperationIds.size > 0,
    [report, selectedOperationIds]
  );

  useEffect(() => {
    void fetchJson<MigrationAuditReport[]>("/api/migration/reports")
      .then(setAuditReports)
      .catch(() => setAuditReports([]));
  }, []);

  async function handlePreview(event?: FormEvent<HTMLFormElement>) {
    event?.preventDefault();
    setBusy("preview");
    setMessage(null);
    setApplied(null);
    setConnectionValidation({});

    try {
      const nextReport = await fetchJson<MigrationReport>("/api/migration/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ sourceKind, sourceName, payloadJson })
      });
      setReport(nextReport);
      setSelectedOperationIds(new Set(nextReport.operations.filter(isSelectableOperation).map((operation) => operation.id)));
      setMessage(nextReport.valid ? "Preview ready. Review every create, skip, and warning before applying." : "Preview found issues.");
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
        body: JSON.stringify({ sourceKind, sourceName, payloadJson, selectedOperationIds: [...selectedOperationIds] })
      });
      setApplied(result);
      setReport(result.report);
      setConnectionValidation({});
      const reports = await fetchJson<MigrationAuditReport[]>("/api/migration/reports").catch(() => []);
      setAuditReports(reports);
      const createdCount = result.applied.filter((item) => item.result === "created").length;
      setMessage(result.report.errors.length > 0
        ? "Migration paused safely. Review the report and audit, then retry when you are ready."
        : `${createdCount} selected item${createdCount === 1 ? "" : "s"} imported. Deluno skipped anything already present.`);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Migration apply failed.");
    } finally {
      setBusy(null);
    }
  }

  const importedConnections = useMemo(
    () => (applied?.applied ?? []).filter(
      (item) => item.result === "created" && item.createdId && (item.targetType === "indexer" || item.targetType === "download-client")
    ),
    [applied]
  );

  async function handleValidateImportedConnections() {
    if (importedConnections.length === 0) return;
    setBusy("validate");
    setMessage(null);

    const results: Record<string, ConnectionValidationResult> = {};
    await Promise.all(importedConnections.map(async (item) => {
      const collection = item.targetType === "indexer" ? "indexers" : "download-clients";
      try {
        results[item.operationId] = await fetchJson<ConnectionValidationResult>(`/api/${collection}/${item.createdId}/test`, {
          method: "POST"
        });
      } catch (error) {
        results[item.operationId] = {
          healthStatus: "needs review",
          message: error instanceof Error ? error.message : "Connection test could not run."
        };
      }
    }));

    setConnectionValidation(results);
    const healthyCount = Object.values(results).filter((result) => result.healthStatus === "healthy").length;
    setMessage(
      healthyCount === importedConnections.length
        ? "Every imported connection responded successfully."
        : `${healthyCount} of ${importedConnections.length} imported connection${importedConnections.length === 1 ? "" : "s"} passed. Review anything marked for attention.`
    );
    setBusy(null);
  }

  return (
    <SettingsShell
      title="Migration Assistant"
      description="Move from Radarr, Sonarr, Prowlarr, Recyclarr, or compatible exports without overwriting existing Deluno configuration."
    >
      <div className="settings-split settings-split-config-heavy">
        <Card className="settings-panel">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <FileJson className="h-5 w-5 text-primary" />
              Import source
            </CardTitle>
            <CardDescription>
              Paste an exported JSON snapshot. Deluno previews the changes first and never overwrites matching configuration silently.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form className="space-y-[var(--field-group-pad)]" onSubmit={(event) => void handlePreview(event)}>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label="Source type" description="The application you're exporting configuration from: Radarr, Sonarr, Prowlarr, Recyclarr, or a compatible JSON format.">
                  <select
                    value={sourceKind}
                    onChange={(event) => {
                      setSourceKind(event.target.value);
                      setSourceName(SOURCE_OPTIONS.find((item) => item.value === event.target.value)?.label ?? "External stack");
                    }}
                    className="density-control-text h-[var(--control-height)] w-full rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none"
                  >
                    {SOURCE_OPTIONS.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Source name" description="A friendly label for this import (e.g., 'Home Radarr', 'Work Sonarr'). Used to identify this import in your history.">
                  <Input value={sourceName} onChange={(event) => setSourceName(event.target.value)} />
                </Field>
              </div>

              <Field label="Export JSON" description="Paste the exported configuration JSON from your source application. Deluno will preview all changes before importing.">
                <textarea
                  value={payloadJson}
                  onChange={(event) => setPayloadJson(event.target.value)}
                  spellCheck={false}
                  placeholder={SAMPLE_PAYLOAD}
                  className="density-control-text min-h-[34rem] w-full resize-y rounded-xl border border-hairline bg-surface-2 px-[var(--field-pad-x)] py-4 font-mono text-foreground outline-none placeholder:text-muted-foreground/45"
                />
              </Field>

              <div className="flex flex-wrap gap-2">
                <Button type="submit" disabled={busy !== null}>
                  {busy === "preview" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <ShieldCheck className="h-4 w-4" />}
                  Preview import
                </Button>
                <Button type="button" variant="secondary" onClick={() => setPayloadJson(SAMPLE_PAYLOAD)}>
                  Load example
                </Button>
                <Button type="button" variant="outline" onClick={() => void handleApply()} disabled={!canApply || busy !== null}>
                  {busy === "apply" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <ArrowRight className="h-4 w-4" />}
                  Apply selected changes ({selectedOperationIds.size})
                </Button>
              </div>
            </form>
          </CardContent>
        </Card>

        <div className="settings-side-stack">
          <Card>
            <CardHeader>
              <CardTitle>Safety model</CardTitle>
              <CardDescription>Migration is deliberately non-destructive.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3 density-help text-muted-foreground">
              <SafetyRow>Preview and apply run the same mapping code.</SafetyRow>
              <SafetyRow>Existing libraries, profiles, sources, and clients are skipped, not overwritten.</SafetyRow>
              <SafetyRow>Missing host, URL, or feed data is reported as unsupported instead of guessed.</SafetyRow>
              <SafetyRow>Detected monitored/wanted titles are reported for metadata reconciliation.</SafetyRow>
            </CardContent>
          </Card>

          {message ? (
            <Card>
              <CardContent className="pt-[var(--tile-pad)] density-help text-muted-foreground">{message}</CardContent>
            </Card>
          ) : null}

          {report ? <MigrationSummary report={report} /> : null}

          {applied ? (
            <Card>
              <CardHeader>
                <CardTitle>Applied changes</CardTitle>
                <CardDescription>Each saved or failed stage returned by the backend. A failed stage stops the migration safely; retrying skips matching saved items.</CardDescription>
              </CardHeader>
              <CardContent className="space-y-2">
                {applied.applied.length === 0 ? (
                  <p className="density-help text-muted-foreground">No new items were created.</p>
                ) : (
                  applied.applied.map((item) => (
                    <div key={item.operationId} className="rounded-xl border border-hairline bg-surface-1 p-3">
                      <p className="font-semibold text-foreground">{item.name}</p>
                      <p className="mt-1 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                        {item.targetType} · {item.result}{item.createdId ? ` · ${item.createdId}` : ""}
                      </p>
                    </div>
                  ))
                )}
                {importedConnections.length > 0 ? (
                  <div className="mt-4 rounded-xl border border-primary/20 bg-primary/5 p-3">
                    <p className="font-semibold text-foreground">Validate imported connections</p>
                    <p className="mt-1 density-help text-muted-foreground">
                      Deluno tests the saved search sources and download clients using their protected credentials. Secrets never appear in this screen or the migration audit.
                    </p>
                    <Button type="button" size="sm" className="mt-3" disabled={busy !== null} onClick={() => void handleValidateImportedConnections()}>
                      {busy === "validate" ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <ShieldCheck className="h-4 w-4" />}
                      Validate imported connections ({importedConnections.length})
                    </Button>
                    {importedConnections.map((item) => {
                      const result = connectionValidation[item.operationId];
                      if (!result) return null;
                      return (
                        <div key={item.operationId} className="mt-3 rounded-lg border border-hairline bg-background/40 p-2.5">
                          <p className="text-sm font-semibold text-foreground">{item.name} · {result.healthStatus}</p>
                          <p className="mt-1 density-help text-muted-foreground">{result.message}</p>
                        </div>
                      );
                    })}
                  </div>
                ) : null}
              </CardContent>
            </Card>
          ) : null}

          {auditReports.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>Applied migration history</CardTitle>
                <CardDescription>Immutable, redacted records. Preview-only runs are never stored.</CardDescription>
              </CardHeader>
              <CardContent className="space-y-2">
                {auditReports.map((audit) => (
                  <button
                    key={audit.id}
                    type="button"
                    onClick={() => {
                      setReport(audit.preflightReport);
                      setApplied({ report: audit.resultReport, applied: audit.applied, auditReportId: audit.id });
                      setMessage(`Showing applied migration record from ${new Date(audit.appliedUtc).toLocaleString()}.`);
                    }}
                    className="w-full rounded-xl border border-hairline bg-surface-1 p-3 text-left transition hover:border-primary/35"
                  >
                    <p className="font-semibold text-foreground">{audit.sourceName}</p>
                    <p className="mt-1 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                      {new Date(audit.appliedUtc).toLocaleString()} · {audit.applied.filter((item) => item.result === "created").length} created · {audit.applied.filter((item) => item.result === "failed").length} failed
                    </p>
                  </button>
                ))}
              </CardContent>
            </Card>
          ) : null}
        </div>
      </div>

      {report ? <MigrationOperations operations={report.operations} selectedOperationIds={selectedOperationIds} onSelectionChange={setSelectedOperationIds} /> : null}
    </SettingsShell>
  );
}

function MigrationSummary({ report }: { report: MigrationReport }) {
  const stats = [
    { label: "Create", value: report.summary.createCount, variant: "success" as const },
    { label: "Skip", value: report.summary.skipCount, variant: "default" as const },
    { label: "Unsupported", value: report.summary.unsupportedCount, variant: "warning" as const },
    { label: "Titles", value: report.summary.titleCount, variant: "info" as const }
  ];

  return (
    <Card>
      <CardHeader>
        <CardTitle>Preview summary</CardTitle>
        <CardDescription>{report.sourceName} · {report.sourceKind}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-[var(--page-gap)]">
        <div className="grid grid-cols-2 gap-3">
          {stats.map((stat) => (
            <div key={stat.label} className="rounded-xl border border-hairline bg-surface-1 p-3">
              <p className="text-[length:var(--section-eyebrow-size)] font-bold uppercase tracking-[0.16em] text-muted-foreground">
                {stat.label}
              </p>
              <p className="tabular mt-2 font-display text-[length:var(--type-title-sm)] font-semibold text-foreground">
                {stat.value}
              </p>
            </div>
          ))}
        </div>
        {report.errors.map((error) => (
          <Notice key={error} variant="destructive">{error}</Notice>
        ))}
        {report.warnings.map((warning) => (
          <Notice key={warning} variant="warning">{warning}</Notice>
        ))}
      </CardContent>
    </Card>
  );
}

function isSelectableOperation(operation: MigrationReportOperation) {
  return operation.canApply || (operation.category === "catalog" && operation.targetType === "monitored-state");
}

function MigrationOperations({
  operations,
  selectedOperationIds,
  onSelectionChange
}: {
  operations: MigrationReportOperation[];
  selectedOperationIds: Set<string>;
  onSelectionChange: (next: Set<string>) => void;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Change report</CardTitle>
        <CardDescription>Select only the safe changes you want to apply. Skipped, unsupported, and conflicting items remain review-only.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {operations.length === 0 ? (
          <p className="density-help text-muted-foreground">No supported configuration was found in this payload yet.</p>
        ) : (
          operations.map((operation) => {
            const selectable = isSelectableOperation(operation);
            const selected = selectedOperationIds.has(operation.id);
            return (
            <div key={operation.id} className="rounded-2xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.75)]">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge variant={getActionVariant(operation.action)}>{operation.action}</Badge>
                    <Badge variant="default">{operation.targetType}</Badge>
                  </div>
                  <p className="mt-3 font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
                    {operation.name}
                  </p>
                  <p className="mt-1 density-help text-muted-foreground">{operation.reason}</p>
                </div>
                {selectable ? (
                  <label className="flex items-center gap-2 text-sm font-semibold text-foreground">
                    <input
                      type="checkbox"
                      aria-label={`Apply ${operation.name}`}
                      checked={selected}
                      onChange={(event) => {
                        const next = new Set(selectedOperationIds);
                        if (event.target.checked) next.add(operation.id);
                        else next.delete(operation.id);
                        onSelectionChange(next);
                      }}
                    />
                    Apply
                  </label>
                ) : (
                  <TriangleAlert className="h-5 w-5 text-muted-foreground" />
                )}
              </div>
              {operation.warnings.length > 0 ? (
                <div className="mt-3 space-y-2">
                  {operation.warnings.map((warning) => (
                    <Notice key={warning} variant="warning">{warning}</Notice>
                  ))}
                </div>
              ) : null}
              {Object.keys(operation.data).length > 0 ? (
                <div className="mt-3 grid gap-2 md:grid-cols-2 xl:grid-cols-4">
                  {Object.entries(operation.data)
                    .filter(([, value]) => value)
                    .map(([key, value]) => (
                      <div key={key} className="rounded-xl border border-hairline bg-card px-3 py-2">
                        <p className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.16em] text-muted-foreground">{key}</p>
                        <p className="mt-1 truncate font-mono text-[length:var(--type-caption)] text-foreground" title={value ?? undefined}>{value}</p>
                      </div>
                    ))}
                </div>
              ) : null}
            </div>
            );
          })
        )}
      </CardContent>
    </Card>
  );
}

function Field({ children, description, label }: { children: React.ReactNode; description?: string; label: string }) {
  return (
    <label className="block space-y-2">
      <span className="text-[length:var(--type-caption)] font-bold uppercase tracking-[0.16em] text-muted-foreground">{label}</span>
      {children}
      {description && <InputDescription>{description}</InputDescription>}
    </label>
  );
}

function SafetyRow({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex gap-3 rounded-xl border border-hairline bg-surface-1 p-3">
      <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-success" />
      <p>{children}</p>
    </div>
  );
}

function Notice({ children, variant }: { children: React.ReactNode; variant: "warning" | "destructive" }) {
  return (
    <div className={`rounded-xl border px-3 py-2 density-help ${variant === "warning" ? "border-warning/30 bg-warning/10 text-warning" : "border-destructive/30 bg-destructive/10 text-destructive"}`}>
      {children}
    </div>
  );
}

function getActionVariant(action: string) {
  if (action === "create") return "success";
  if (action === "unsupported" || action === "conflict") return "warning";
  if (action === "report") return "info";
  return "default";
}
