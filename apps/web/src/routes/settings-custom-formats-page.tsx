/**
 * Custom Formats page — Deluno's simplified replacement for the
 * Radarr/Sonarr custom format editor.
 *
 * Three sections:
 *   1. Format library — 80+ TRaSH pre-built formats, organised by category
 *   2. My formats — user-created custom formats
 *   3. Active in profiles — overview of which profiles use which formats
 */

import { useState } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import { BookOpen, CheckCircle2, FlaskConical, LoaderCircle, PackagePlus, ShieldCheck, Sparkles, Trash2 } from "lucide-react";
import { SettingsShell } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { EmptyState } from "../components/shell/empty-state";
import { CFLibraryBrowser } from "../components/app/cf-library-browser";
import { CFCreator, type CFDraft } from "../components/app/cf-creator";
import { toast } from "../components/shell/toaster";
import {
  CUSTOM_FORMAT_BUNDLES,
  findBundledCF,
  type BundledCF,
  type CustomFormatBundle,
} from "../lib/trash-guide-data";
import {
  emptyPlatformSettingsSnapshot,
  fetchJson,
  type CustomFormatItem,
  type LibraryItem,
  type PlatformSettingsSnapshot,
  type QualityProfileItem,
} from "../lib/api";
import { settingsOverviewLoader } from "./settings-overview-page";
import { cn } from "../lib/utils";
import { authedFetch } from "../lib/use-auth";
import { RouteSkeleton } from "../components/shell/skeleton";

/* ── Loader ──────────────────────────────────────────────────────── */
interface LoaderData {
  libraries: LibraryItem[];
  qualityProfiles: QualityProfileItem[];
  customFormats: CustomFormatItem[];
  settings: PlatformSettingsSnapshot;
}

export async function settingsCustomFormatsLoader(): Promise<LoaderData> {
  const [overview, customFormats] = await Promise.all([
    settingsOverviewLoader(),
    fetchJson<CustomFormatItem[]>("/api/custom-formats"),
  ]);
  return { ...overview, customFormats };
}

/* ── Condition helpers ───────────────────────────────────────────── */
type ConditionType = "releaseTitle" | "source" | "resolution" | "hdr" | "codec" | "releaseGroup" | "language";
interface ConditionObj { type: ConditionType; value: string; negate?: boolean; required?: boolean }

function serializeConditions(conditions: ConditionObj[]): string {
  return JSON.stringify(conditions);
}

function conditionSummary(rawConditions: string | null | undefined): string {
  if (!rawConditions) return "No conditions";
  const trimmed = rawConditions.trim();
  if (trimmed.startsWith("[")) {
    try {
      const arr = JSON.parse(trimmed) as ConditionObj[];
      return `${arr.length} condition${arr.length !== 1 ? "s" : ""}`;
    } catch { /* fall through */ }
  }
  return trimmed.split("\n").filter(Boolean).length + " rules (legacy)";
}

/* ── Dry-run types ───────────────────────────────────────────────── */
interface DryRunResult {
  formatId: string;
  formatName: string;
  score: number;
  isMatch: boolean;
  matchedConditions: string[];
  missedConditions: string[];
}

/* ── Tab ─────────────────────────────────────────────────────────── */
type Tab = "library" | "mine" | "create" | "test";

const TABS: { id: Tab; label: string; description: string }[] = [
  {
    id: "library",
    label: "Advanced library",
    description: "Pre-built format rules organised by category. Use this when presets do not cover your target.",
  },
  {
    id: "mine",
    label: "My formats",
    description: "Custom formats you've created yourself.",
  },
  {
    id: "create",
    label: "Create format",
    description: "Build a new custom format — no regex required for basic use.",
  },
  {
    id: "test",
    label: "Test a release",
    description: "Paste a release name and see which of your formats would match it and why.",
  },
];

/* ── Score badge ─────────────────────────────────────────────────── */
function ScoreBadge({ score }: { score: number }) {
  if (score <= -10000)
    return (
      <span className="rounded-full border border-destructive/20 bg-destructive/10 px-2 py-0.5 font-mono text-[10px] font-bold text-destructive">
        BLOCKED
      </span>
    );
  return (
    <span
      className={cn(
        "rounded-full border px-2 py-0.5 font-mono text-[10px] font-bold",
        score > 0
          ? "border-primary/20 bg-primary/10 text-primary"
          : "border-hairline text-muted-foreground"
      )}
    >
      {score > 0 ? "+" : ""}
      {score.toLocaleString()}
    </span>
  );
}

/* ── Page ────────────────────────────────────────────────────────── */
export function SettingsCustomFormatsPage() {
  const loaderData = useLoaderData() as LoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;
  const { customFormats } = loaderData;
  const revalidator = useRevalidator();

  const [tab, setTab] = useState<Tab>("library");
  const [busyKey, setBusyKey] = useState<string | null>(null);

  /**
   * Library selections — maps trashId → score. In real implementation,
   * these would be synced to the quality profile API.
   */
  const [librarySelections, setLibrarySelections] = useState<Map<string, number>>(() => {
    const m = new Map<string, number>();
    for (const cf of customFormats) {
      if (cf.trashId) m.set(cf.trashId, cf.score);
    }
    return m;
  });

  /* ── Library selection handlers ── */
  async function handleLibraryAdd(cf: BundledCF, score: number) {
    setLibrarySelections((prev) => new Map(prev).set(cf.trashId, score));
    try {
      // Serialize patterns as structured JSON conditions (releaseTitle type)
      const conditions = serializeConditions(
        cf.patterns.map((p) => ({ type: "releaseTitle" as ConditionType, value: p, negate: false, required: true }))
      );
      const res = await authedFetch("/api/custom-formats", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: cf.name,
          mediaType: "movies",
          score,
          trashId: cf.trashId,
          conditions,
          upgradeAllowed: true,
        }),
      });
      if (!res.ok) throw new Error("Could not add format.");
      toast.success(`Added "${cf.name}" to your formats`);
      revalidator.revalidate();
    } catch (e) {
      setLibrarySelections((prev) => { const m = new Map(prev); m.delete(cf.trashId); return m; });
      toast.error(e instanceof Error ? e.message : "Could not add format.");
    }
  }

  async function handleBundleApply(bundle: CustomFormatBundle) {
    const formats = bundle.includes
      .map((entry) => {
        const cf = findBundledCF(entry.trashId);
        return cf ? { cf, score: entry.score ?? cf.defaultScore } : null;
      })
      .filter((entry): entry is { cf: BundledCF; score: number } => Boolean(entry));

    const missing = formats.filter(({ cf }) => !librarySelections.has(cf.trashId));
    if (missing.length === 0) {
      toast.info(`"${bundle.name}" is already applied.`);
      return;
    }

    setBusyKey(`bundle:${bundle.id}`);
    setLibrarySelections((prev) => {
      const next = new Map(prev);
      for (const { cf, score } of missing) next.set(cf.trashId, score);
      return next;
    });

    try {
      for (const { cf, score } of missing) {
        const conditions = serializeConditions(
          cf.patterns.map((p) => ({ type: "releaseTitle" as ConditionType, value: p, negate: false, required: true }))
        );
        const res = await authedFetch("/api/custom-formats", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            name: cf.name,
            mediaType: bundle.mediaType === "tv" ? "tv" : "movies",
            score,
            trashId: cf.trashId,
            conditions,
            upgradeAllowed: true,
          }),
        });
        if (!res.ok) throw new Error(`Could not add ${cf.name}.`);
      }

      toast.success(`Applied "${bundle.name}" (${missing.length} formats)`);
      revalidator.revalidate();
    } catch (error) {
      setLibrarySelections((prev) => {
        const next = new Map(prev);
        for (const { cf } of missing) next.delete(cf.trashId);
        return next;
      });
      toast.error(error instanceof Error ? error.message : "Preset could not be applied.");
    } finally {
      setBusyKey(null);
    }
  }

  function handleLibraryRemove(trashId: string) {
    const cf = customFormats.find((c) => c.trashId === trashId);
    if (cf) void handleDelete(cf.id, trashId);
    else setLibrarySelections((prev) => { const m = new Map(prev); m.delete(trashId); return m; });
  }

  function handleLibraryScoreChange(trashId: string, score: number) {
    setLibrarySelections((prev) => new Map(prev).set(trashId, score));
  }

  /* ── User CF handlers ── */
  async function handleDelete(id: string, trashId?: string) {
    setBusyKey(`delete:${id}`);
    try {
      const res = await authedFetch(`/api/custom-formats/${id}`, { method: "DELETE" });
      if (!res.ok && res.status !== 204) throw new Error("Could not remove format.");
      if (trashId) {
        setLibrarySelections((prev) => { const m = new Map(prev); m.delete(trashId); return m; });
      }
      toast.success("Custom format removed");
      revalidator.revalidate();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Could not remove format.");
    } finally {
      setBusyKey(null);
    }
  }

  async function handleCreatorSave(draft: CFDraft) {
    // Serialize conditions as structured JSON array for the new matcher
    const conditions = serializeConditions(
      draft.conditions.map((c) => {
        const type: ConditionType = (c as { conditionType?: ConditionType }).conditionType ?? "releaseTitle";
        const value =
          c.mode === "regex"
            ? c.value
            : c.mode === "contains"
            ? c.value
            : c.mode === "starts-with"
            ? `^${c.value}`
            : c.mode === "ends-with"
            ? `${c.value}$`
            : c.value;
        return { type, value, negate: c.negate ?? false, required: true };
      })
    );

    const res = await authedFetch("/api/custom-formats", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: draft.name,
        mediaType: "movies",
        score: 100,
        conditions,
        upgradeAllowed: true,
      }),
    });
    if (!res.ok) throw new Error("Could not create format.");
    toast.success(`Format "${draft.name}" created`);
    revalidator.revalidate();
    setTab("mine");
  }

  const activeTab = TABS.find((t) => t.id === tab)!;

  return (
    <SettingsShell
      title="Custom Formats"
      description="Score releases by their traits. Add pre-built formats from the library or create your own — no regex or JSON required."
    >
      <PresetBundles
        selections={librarySelections}
        busyKey={busyKey}
        onApply={handleBundleApply}
      />

      {/* Tab bar */}
      <div className="flex gap-1 rounded-2xl border border-hairline bg-surface-1 p-1">
        {TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            onClick={() => setTab(t.id)}
            className={cn(
              "flex-1 rounded-xl px-4 py-2.5 text-[13px] font-medium transition-all",
              tab === t.id
                ? "bg-background text-foreground shadow-sm"
                : "text-muted-foreground hover:text-foreground"
            )}
          >
            {t.label}
          </button>
        ))}
      </div>

      {/* Tab description */}
      <p className="text-[13px] text-muted-foreground">{activeTab.description}</p>

      {/* ── Library tab ── */}
      {tab === "library" && (
        <CFLibraryBrowser
          selections={librarySelections}
          onAdd={handleLibraryAdd}
          onRemove={handleLibraryRemove}
          onScoreChange={handleLibraryScoreChange}
        />
      )}

      {/* ── My formats tab ── */}
      {tab === "mine" && (
        <div className="space-y-[calc(var(--field-group-pad)*0.9)]">
          {customFormats.length > 0 ? (
            <div className="space-y-2.5">
              {customFormats.map((cf) => {
                const bundled = cf.trashId ? findBundledCF(cf.trashId) : undefined;
                return (
                  <div
                    key={cf.id}
                    className="group flex items-start gap-3 rounded-2xl border border-hairline bg-surface-1 p-4"
                  >
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2 flex-wrap">
                        <p className="font-medium text-foreground">{cf.name}</p>
                        <ScoreBadge score={cf.score} />
                        {bundled && (
                          <span className="flex items-center gap-1 rounded-full border border-emerald-500/20 bg-emerald-500/10 px-2 py-0.5 text-[9.5px] font-bold uppercase tracking-wide text-emerald-400">
                            <BookOpen className="h-2.5 w-2.5" />
                            TRaSH built-in
                          </span>
                        )}
                      </div>
                      {bundled && (
                        <p className="mt-0.5 text-[11.5px] text-muted-foreground">{bundled.description}</p>
                      )}
                      <p className="mt-1 text-[11px] text-muted-foreground/70">
                        {cf.mediaType === "tv" ? "TV" : "Movies"} · {conditionSummary(cf.conditions)}
                      </p>
                    </div>
                    <Button
                      variant="ghost"
                      size="icon"
                      onClick={() => void handleDelete(cf.id, cf.trashId ?? undefined)}
                      disabled={busyKey === `delete:${cf.id}`}
                      className="opacity-0 group-hover:opacity-100 transition-opacity"
                    >
                      {busyKey === `delete:${cf.id}` ? (
                        <LoaderCircle className="h-4 w-4 animate-spin" />
                      ) : (
                        <Trash2 className="h-4 w-4 text-muted-foreground" />
                      )}
                    </Button>
                  </div>
                );
              })}
            </div>
          ) : (
            <EmptyState
              size="sm"
              variant="custom"
              title="No formats yet"
              description="Browse the library to add TRaSH pre-built formats, or create your own."
              action={
                <button
                  type="button"
                  onClick={() => setTab("library")}
                  className="rounded-xl border border-primary/30 bg-primary/10 px-4 py-2 text-[13px] font-medium text-foreground hover:bg-primary/20 transition-colors"
                >
                  Browse library
                </button>
              }
              secondaryAction={
                <button
                  type="button"
                  onClick={() => setTab("create")}
                  className="text-[12.5px] text-muted-foreground hover:text-foreground underline underline-offset-2"
                >
                  Create format
                </button>
              }
            />
          )}
        </div>
      )}

      {/* ── Create tab ── */}
      {tab === "create" && (
        <div className="rounded-2xl border border-hairline bg-surface-1 p-6">
          <CFCreator
            onSave={handleCreatorSave}
            onCancel={() => setTab("mine")}
          />
        </div>
      )}

      {/* ── Test tab ── */}
      {tab === "test" && (
        <DryRunPanel formats={customFormats} />
      )}
    </SettingsShell>
  );
}

/* ── Dry-run panel ───────────────────────────────────────────────── */
function DryRunPanel({ formats }: { formats: CustomFormatItem[] }) {
  const [releaseName, setReleaseName] = useState("");
  const [results, setResults] = useState<DryRunResult[] | null>(null);
  const [loading, setLoading] = useState(false);

  async function handleTest() {
    if (!releaseName.trim()) return;
    setLoading(true);
    try {
      const data = await fetchJson<DryRunResult[]>("/api/custom-formats/dry-run", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ releaseName: releaseName.trim() }),
      });
      setResults(data);
    } catch {
      toast.error("Dry-run failed.");
    } finally {
      setLoading(false);
    }
  }

  const matched = results?.filter((r) => r.isMatch) ?? [];
  const missed = results?.filter((r) => !r.isMatch) ?? [];

  return (
    <div className="space-y-6">
      {/* Input */}
      <div className="rounded-2xl border border-hairline bg-surface-1 p-5">
        <p className="mb-2 text-[12px] font-semibold uppercase tracking-widest text-muted-foreground">Release name</p>
        <div className="flex gap-3">
          <input
            type="text"
            value={releaseName}
            onChange={(e) => setReleaseName(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && void handleTest()}
            placeholder="e.g. Movie.Title.2024.2160p.UHD.BluRay.DV.HDR.x265-GROUP"
            className="flex-1 rounded-xl border border-hairline bg-background px-3 py-2 font-mono text-[12.5px] text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus:ring-1 focus:ring-primary/50"
          />
          <Button onClick={() => void handleTest()} disabled={loading || !releaseName.trim()} className="gap-2">
            {loading ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <FlaskConical className="h-4 w-4" />}
            Test
          </Button>
        </div>
        {formats.length === 0 && (
          <p className="mt-2 text-[11.5px] text-amber-400">You have no formats yet — add some from the Library tab first.</p>
        )}
      </div>

      {results && (
        <div className="space-y-4">
          {/* Summary row */}
          <div className="flex items-center gap-4 rounded-2xl border border-hairline bg-surface-1 px-4 py-3">
            <span className="text-[13px] font-medium text-foreground">{results.length} format{results.length !== 1 ? "s" : ""} evaluated</span>
            <span className="rounded-full border border-primary/25 bg-primary/10 px-2 py-0.5 font-mono text-[11px] font-bold text-primary">
              {matched.length} matched · {matched.reduce((s, r) => s + r.score, 0) > 0 ? "+" : ""}{matched.reduce((s, r) => s + r.score, 0)} pts
            </span>
            {missed.length > 0 && (
              <span className="rounded-full border border-hairline px-2 py-0.5 font-mono text-[11px] text-muted-foreground">
                {missed.length} missed
              </span>
            )}
          </div>

          {/* Matched formats */}
          {matched.length > 0 && (
            <div className="space-y-2">
              <p className="text-[11px] font-bold uppercase tracking-widest text-primary">Matched</p>
              {matched.map((r) => (
                <div key={r.formatId} className="rounded-2xl border border-primary/20 bg-primary/5 p-4">
                  <div className="flex items-center gap-2">
                    <CheckCircle2 className="h-4 w-4 text-primary" />
                    <span className="font-medium text-foreground">{r.formatName}</span>
                    <span className="rounded-full border border-primary/20 bg-primary/10 px-2 py-0.5 font-mono text-[10px] font-bold text-primary">
                      {r.score > 0 ? "+" : ""}{r.score}
                    </span>
                  </div>
                  {r.matchedConditions.length > 0 && (
                    <div className="mt-2 flex flex-wrap gap-1.5">
                      {r.matchedConditions.map((c) => (
                        <span key={c} className="rounded-lg border border-primary/15 bg-primary/5 px-2 py-0.5 font-mono text-[10px] text-primary">
                          ✓ {c}
                        </span>
                      ))}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}

          {/* Missed formats */}
          {missed.length > 0 && (
            <div className="space-y-2">
              <p className="text-[11px] font-bold uppercase tracking-widest text-muted-foreground">Did not match</p>
              {missed.map((r) => (
                <div key={r.formatId} className="rounded-2xl border border-hairline bg-surface-1 p-3">
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-muted-foreground">{r.formatName}</span>
                    <span className="rounded-full border border-hairline px-2 py-0.5 font-mono text-[10px] text-muted-foreground">{r.score > 0 ? "+" : ""}{r.score}</span>
                  </div>
                  {r.missedConditions.length > 0 && (
                    <div className="mt-1.5 flex flex-wrap gap-1.5">
                      {r.missedConditions.map((c) => (
                        <span key={c} className="rounded-lg border border-hairline px-2 py-0.5 font-mono text-[10px] text-muted-foreground">
                          ✗ {c}
                        </span>
                      ))}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function PresetBundles({
  selections,
  busyKey,
  onApply,
}: {
  selections: Map<string, number>;
  busyKey: string | null;
  onApply: (bundle: CustomFormatBundle) => Promise<void>;
}) {
  return (
    <section className="rounded-2xl border border-hairline bg-card shadow-sm">
      <div className="border-b border-hairline px-[var(--tile-pad)] py-[calc(var(--tile-pad)*0.85)]">
        <div className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
          <div>
            <p className="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-[0.18em] text-primary">
              <Sparkles className="h-3.5 w-3.5" />
              Recommended presets
            </p>
            <h2 className="mt-1 font-display text-xl font-semibold tracking-tight text-foreground">Start with a goal, not a rules list</h2>
            <p className="mt-1 max-w-3xl text-sm leading-relaxed text-muted-foreground">
              These presets bundle the common release rules users normally copy from guides. Apply one now, then fine-tune individual formats only if needed.
            </p>
          </div>
          <div className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-xs text-muted-foreground">
            <span className="font-semibold text-foreground">{selections.size}</span> active format rules
          </div>
        </div>
      </div>

      <div className="grid gap-3 p-[var(--tile-pad)] md:grid-cols-2 xl:grid-cols-3">
        {CUSTOM_FORMAT_BUNDLES.map((bundle) => {
          const resolved = bundle.includes
            .map((entry) => findBundledCF(entry.trashId))
            .filter((cf): cf is BundledCF => Boolean(cf));
          const appliedCount = resolved.filter((cf) => selections.has(cf.trashId)).length;
          const totalCount = resolved.length;
          const isComplete = totalCount > 0 && appliedCount === totalCount;
          const isBusy = busyKey === `bundle:${bundle.id}`;

          return (
            <article
              key={bundle.id}
              className={cn(
                "flex min-h-[230px] flex-col rounded-2xl border bg-surface-1 p-[calc(var(--tile-pad)*0.8)] transition-all",
                isComplete ? "border-primary/30 bg-primary/5" : "border-hairline hover:border-primary/25"
              )}
            >
              <div className="flex items-start justify-between gap-3">
                <div>
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="rounded-full border border-hairline bg-background/60 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
                      {bundle.level}
                    </span>
                    <span className="rounded-full border border-hairline bg-background/60 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
                      {bundle.mediaType === "all" ? "Movies + TV" : bundle.mediaType}
                    </span>
                  </div>
                  <h3 className="mt-3 font-display text-lg font-semibold tracking-tight text-foreground">{bundle.name}</h3>
                </div>
                {isComplete ? (
                  <CheckCircle2 className="h-5 w-5 shrink-0 text-primary" />
                ) : (
                  <PackagePlus className="h-5 w-5 shrink-0 text-muted-foreground" />
                )}
              </div>

              <p className="mt-2 text-sm leading-relaxed text-muted-foreground">{bundle.description}</p>
              <p className="mt-3 rounded-xl border border-hairline bg-background/40 px-3 py-2 text-xs leading-relaxed text-muted-foreground">
                <span className="font-semibold text-foreground">Best for:</span> {bundle.bestFor}
              </p>

              {bundle.warnings?.length ? (
                <div className="mt-3 space-y-1">
                  {bundle.warnings.map((warning) => (
                    <p key={warning} className="text-xs leading-relaxed text-warning">{warning}</p>
                  ))}
                </div>
              ) : null}

              <div className="mt-auto pt-4">
                <div className="mb-2 flex items-center justify-between text-xs text-muted-foreground">
                  <span>{appliedCount}/{totalCount} applied</span>
                  <span>{Math.max(totalCount - appliedCount, 0)} remaining</span>
                </div>
                <div className="mb-3 h-1.5 overflow-hidden rounded-full bg-surface-2">
                  <div
                    className="h-full rounded-full bg-primary transition-all"
                    style={{ width: `${totalCount ? (appliedCount / totalCount) * 100 : 0}%` }}
                  />
                </div>
                <Button
                  type="button"
                  className="w-full"
                  variant={isComplete ? "outline" : "default"}
                  disabled={isBusy}
                  onClick={() => void onApply(bundle)}
                >
                  {isBusy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : isComplete ? <ShieldCheck className="h-4 w-4" /> : <PackagePlus className="h-4 w-4" />}
                  {isComplete ? "Applied" : "Apply preset"}
                </Button>
              </div>
            </article>
          );
        })}
      </div>
    </section>
  );
}
