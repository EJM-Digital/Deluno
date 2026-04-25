/**
 * Quality Profile Wizard — Deluno's replacement for the confusing
 * quality profile editor found in Radarr/Sonarr.
 *
 * No JSON. No YAML. No reading guides. Anyone can build a profile.
 *
 * Flow:
 *   1. Pick a preset (or start blank)
 *   2. Toggle enhancements (DV, Atmos, IMAX…)
 *   3. Review what gets auto-scored
 *   4. Name and save
 */

import { useState } from "react";
import {
  ArrowLeft,
  ArrowRight,
  Check,
  Film,
  Loader2,
  Save,
  Sparkles,
  Tv,
  X
} from "lucide-react";
import {
  QUALITY_PRESETS,
  QUALITY_TIERS,
  BUNDLED_CUSTOM_FORMATS,
  CUSTOM_FORMAT_BUNDLES,
  type QualityProfilePreset,
  type CustomFormatBundle
} from "../../lib/trash-guide-data";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { toast } from "../shell/toaster";
import { cn } from "../../lib/utils";

/* ── Types ───────────────────────────────────────────────────────── */
export interface ProfileDraft {
  name: string;
  mediaType: "movies" | "tv" | "anime";
  presetId: string | null;
  formatBundleId: string | null;
  qualityOrder: string[];
  cutoffQualityId: string;
  upgradeAllowed: boolean;
  minFormatScore: number;
  cutoffFormatScore: number;
  activeCFs: Map<string, number>; // trashId → score
}

type Step = "preset" | "enhance" | "review";

interface Props {
  onSave: (draft: ProfileDraft) => Promise<void>;
  onCancel: () => void;
  initial?: Partial<ProfileDraft>;
}

/* ── Preset card visual mapping ──────────────────────────────────── */
const PRESET_VISUALS: Record<string, { gradient: string; icon: string; badge?: string }> = {
  "web-1080p":    { gradient: "from-sky-600 to-blue-700",    icon: "📺", badge: "Most popular" },
  "bluray-1080p": { gradient: "from-indigo-600 to-violet-700", icon: "💿" },
  "web-2160p":    { gradient: "from-violet-600 to-purple-700", icon: "✨", badge: "4K" },
  "remux-2160p":  { gradient: "from-amber-600 to-orange-700", icon: "🏆", badge: "Best quality" },
  "web-1080p-tv": { gradient: "from-emerald-600 to-teal-700", icon: "📡" },
  "anime-1080p":  { gradient: "from-pink-600 to-rose-700",   icon: "🎌" },
};

/* ── Optional enhancement toggles ───────────────────────────────── */
interface Enhancement {
  id: string;
  label: string;
  sublabel: string;
  trashId: string;
  score: number;
  requires?: string; // another enhancement that must be on for this to make sense
  conflicts?: string[];
}

const ENHANCEMENTS: Enhancement[] = [
  {
    id: "dv",
    label: "Dolby Vision",
    sublabel: "Prefer DV releases (requires a DV display)",
    trashId: "b337d6812e06c200ec9a2d3cfa9d20a7",
    score: 1000,
  },
  {
    id: "hdr10plus",
    label: "HDR10+",
    sublabel: "Prefer HDR10+ over standard HDR",
    trashId: "caa37d0df9c348912df1fb1d88f9273a",
    score: 100,
  },
  {
    id: "atmos",
    label: "Dolby Atmos",
    sublabel: "Prefer TrueHD Atmos audio when available",
    trashId: "496f355514737f7d83bf7aa4d24f8169",
    score: 10,
  },
  {
    id: "imax",
    label: "IMAX",
    sublabel: "Prefer IMAX Enhanced and IMAX versions",
    trashId: "9de657fd3d327ecf144ec73dfe3a3e9a",
    score: 800,
  },
  {
    id: "block-nogroup",
    label: "Block no-group releases",
    sublabel: "Penalise releases with no named release group",
    trashId: "90a87bd7b54af7e7f0c5c5c5a6b2e9b3",
    score: -10000,
  },
  {
    id: "block-upscale",
    label: "Block fake 4K",
    sublabel: "Penalise AI-upscaled releases claiming to be 4K",
    trashId: "ae9b7c9ebde1f3bd336a8cbd1b5fbd68",
    score: -10000,
  },
];

/* ── Main component ──────────────────────────────────────────────── */
export function QualityProfileWizard({ onSave, onCancel, initial }: Props) {
  const [step, setStep] = useState<Step>("preset");
  const [saving, setSaving] = useState(false);

  const [draft, setDraft] = useState<ProfileDraft>(() => ({
    name: initial?.name ?? "",
    mediaType: initial?.mediaType ?? "movies",
    presetId: initial?.presetId ?? null,
    formatBundleId: initial?.formatBundleId ?? null,
    qualityOrder: initial?.qualityOrder ?? [],
    cutoffQualityId: initial?.cutoffQualityId ?? "",
    upgradeAllowed: initial?.upgradeAllowed ?? true,
    minFormatScore: initial?.minFormatScore ?? 0,
    cutoffFormatScore: initial?.cutoffFormatScore ?? 10000,
    activeCFs: initial?.activeCFs ?? new Map(),
  }));

  const [activeEnhancements, setActiveEnhancements] = useState<Set<string>>(new Set());

  function applyPreset(preset: QualityProfilePreset) {
    const cfs = new Map<string, number>(
      preset.recommendedCFs.map(({ trashId, score }) => [trashId, score])
    );
    setDraft((d) => ({
      ...d,
      presetId: preset.id,
      mediaType: preset.mediaType === "anime" ? "tv" : preset.mediaType,
      qualityOrder: preset.qualityOrder,
      cutoffQualityId: preset.cutoffQualityId,
      upgradeAllowed: preset.upgradeAllowed,
      minFormatScore: preset.minFormatScore,
      cutoffFormatScore: preset.cutoffFormatScore,
      activeCFs: cfs,
      name: d.name || preset.name,
    }));
    setActiveEnhancements(new Set());
  }

  function applyFormatBundle(bundle: CustomFormatBundle) {
    const cfs = new Map<string, number>();
    for (const entry of bundle.includes) {
      const cf = BUNDLED_CUSTOM_FORMATS.find((item) => item.trashId === entry.trashId);
      if (cf) {
        cfs.set(cf.trashId, entry.score ?? cf.defaultScore);
      }
    }

    setDraft((d) => ({
      ...d,
      formatBundleId: bundle.id,
      mediaType: bundle.mediaType === "tv" ? "tv" : d.mediaType,
      activeCFs: cfs,
    }));
    setActiveEnhancements(new Set());
  }

  function toggleEnhancement(enh: Enhancement) {
    const isOn = activeEnhancements.has(enh.id);
    setActiveEnhancements((prev) => {
      const next = new Set(prev);
      if (isOn) next.delete(enh.id);
      else next.add(enh.id);
      return next;
    });
    setDraft((d) => {
      const cfs = new Map(d.activeCFs);
      if (isOn) {
        cfs.delete(enh.trashId);
      } else {
        cfs.set(enh.trashId, enh.score);
      }
      return { ...d, activeCFs: cfs };
    });
  }

  async function handleSave() {
    if (!draft.name.trim()) {
      toast.error("Give your profile a name first");
      return;
    }
    setSaving(true);
    try {
      await onSave(draft);
    } finally {
      setSaving(false);
    }
  }

  const preset = draft.presetId ? QUALITY_PRESETS.find((p) => p.id === draft.presetId) : null;

  return (
    <div className="flex flex-col gap-6">
      {/* Step indicator */}
      <div className="flex items-center gap-2">
        {(["preset", "enhance", "review"] as Step[]).map((s, i) => (
          <div key={s} className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => step !== "preset" && setStep(s)}
              className={cn(
                "flex h-7 w-7 items-center justify-center rounded-full text-[11px] font-bold transition-all",
                s === step
                  ? "bg-primary text-primary-foreground shadow-[0_0_0_3px_hsl(var(--primary)/0.2)]"
                  : steps.indexOf(s) < steps.indexOf(step)
                    ? "bg-success/20 text-success"
                    : "bg-surface-2 text-muted-foreground"
              )}
            >
              {steps.indexOf(s) < steps.indexOf(step) ? <Check className="h-3.5 w-3.5" /> : i + 1}
            </button>
            <span className={cn("text-[12px] font-medium", s === step ? "text-foreground" : "text-muted-foreground")}>
              {STEP_LABELS[s]}
            </span>
            {i < 2 && <ChevronRight className="h-3 w-3 text-muted-foreground/40" />}
          </div>
        ))}
      </div>

      {/* ── Step 1: Pick a preset ── */}
      {step === "preset" && (
        <div className="space-y-[var(--page-gap)]">
          <div>
            <h2 className="text-lg font-semibold text-foreground">Choose a starting point</h2>
            <p className="text-[13px] text-muted-foreground">
              Each preset is based on TRaSH Guide recommendations. You can customise it in the next step.
            </p>
          </div>

          {/* Media type filter */}
          <div className="flex gap-1.5">
            {(["movies", "tv", "anime"] as const).map((type) => (
              <button
                key={type}
                type="button"
                onClick={() => setDraft((d) => ({ ...d, mediaType: type }))}
                className={cn(
                  "flex items-center gap-1.5 rounded-xl border px-3 py-1.5 text-[12.5px] font-medium capitalize transition-all",
                  draft.mediaType === type
                    ? "border-primary/40 bg-primary/10 text-foreground"
                    : "border-hairline text-muted-foreground hover:border-primary/20 hover:text-foreground"
                )}
              >
                {type === "movies" ? <Film className="h-3.5 w-3.5" /> : <Tv className="h-3.5 w-3.5" />}
                {type === "anime" ? "Anime" : type === "tv" ? "TV Shows" : "Movies"}
              </button>
            ))}
          </div>

          {/* Preset cards */}
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {QUALITY_PRESETS
              .filter((p) =>
                draft.mediaType === "anime"
                  ? p.mediaType === "anime"
                  : draft.mediaType === "tv"
                    ? p.mediaType === "tv"
                    : p.mediaType === "movies"
              )
              .map((preset) => {
                const vis = PRESET_VISUALS[preset.id] ?? { gradient: "from-primary to-primary-2", icon: "🎬" };
                const isSelected = draft.presetId === preset.id;

                return (
                  <button
                    key={preset.id}
                    type="button"
                    onClick={() => applyPreset(preset)}
                    className={cn(
                      "group relative flex flex-col gap-3 rounded-2xl border p-4 text-left transition-all duration-200",
                      "hover:border-primary/30 hover:shadow-lg hover:shadow-primary/5",
                      isSelected
                        ? "border-primary/50 shadow-[0_0_0_2px_hsl(var(--primary)/0.2),0_4px_20px_hsl(var(--primary)/0.1)]"
                        : "border-hairline bg-surface-1"
                    )}
                  >
                    {/* Gradient header */}
                    <div
                      className={cn(
                        "flex h-12 w-12 items-center justify-center rounded-xl text-2xl shadow-md",
                        `bg-gradient-to-br ${vis.gradient}`
                      )}
                    >
                      {vis.icon}
                    </div>

                    {/* Selected check */}
                    {isSelected && (
                      <div className="absolute right-3 top-3 flex h-6 w-6 items-center justify-center rounded-full bg-primary text-primary-foreground shadow">
                        <Check className="h-3.5 w-3.5" />
                      </div>
                    )}

                    {/* Badge */}
                    {vis.badge && (
                      <span className="absolute left-4 top-4 mt-9 rounded-full border border-primary/30 bg-primary/10 px-1.5 py-0.5 text-[9.5px] font-bold uppercase tracking-wider text-primary">
                        {vis.badge}
                      </span>
                    )}

                    <div>
                      <p className="font-semibold text-foreground">{preset.name}</p>
                      <p className="mt-0.5 text-[12px] text-muted-foreground">{preset.tagline}</p>
                    </div>

                    <p className="text-[12px] leading-relaxed text-muted-foreground">{preset.description}</p>

                    <ul className="space-y-1">
                      {preset.highlights.map((h) => (
                        <li key={h} className="flex items-start gap-1.5 text-[11.5px] text-muted-foreground">
                          <Check className="mt-0.5 h-3 w-3 shrink-0 text-success" />
                          {h}
                        </li>
                      ))}
                    </ul>
                  </button>
                );
              })}

            {/* Blank / custom option */}
            <button
              type="button"
              onClick={() => {
                setDraft((d) => ({
                  ...d,
                  presetId: "custom",
                  qualityOrder: ["webdl-1080p", "webrip-1080p"],
                  cutoffQualityId: "webdl-1080p",
                  upgradeAllowed: true,
                  activeCFs: new Map(),
                }));
              }}
              className={cn(
                "flex flex-col items-center justify-center gap-3 rounded-2xl border-2 border-dashed p-8 text-center transition-all",
                draft.presetId === "custom"
                  ? "border-primary/40 bg-primary/5"
                  : "border-hairline text-muted-foreground hover:border-primary/25 hover:text-foreground"
              )}
            >
              <Sparkles className="h-6 w-6 opacity-50" />
              <div>
                <p className="font-medium">Build from scratch</p>
                <p className="mt-0.5 text-[12px] text-muted-foreground">Fully custom — you choose every quality and format</p>
              </div>
            </button>
          </div>

          <div className="flex justify-end">
            <Button
              onClick={() => setStep("enhance")}
              disabled={!draft.presetId}
              className="gap-2"
            >
              Next — Enhancements
              <ArrowRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      )}

      {/* ── Step 2: Optional enhancements ── */}
      {step === "enhance" && (
        <div className="space-y-[var(--page-gap)]">
          <div>
            <h2 className="text-lg font-semibold text-foreground">Optional enhancements</h2>
            <p className="text-[13px] text-muted-foreground">
              Toggle what matters to you. Deluno handles the scoring automatically.
            </p>
          </div>

          {/* Quality order quick edit */}
          {draft.qualityOrder.length > 0 && (
            <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
              <p className="mb-3 text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">
                Quality order (most preferred → fallback)
              </p>
              <div className="flex flex-wrap gap-2">
                {draft.qualityOrder.map((qId, i) => {
                  const tier = QUALITY_TIERS.find((t) => t.id === qId);
                  return tier ? (
                    <div
                      key={qId}
                      className={cn(
                        "flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11.5px] font-medium",
                        i === 0
                          ? "border-primary/30 bg-primary/10 text-foreground"
                          : "border-hairline text-muted-foreground"
                      )}
                    >
                      {i === 0 && <Sparkles className="h-3 w-3 text-primary" />}
                      {tier.label}
                    </div>
                  ) : null;
                })}
              </div>
              <p className="mt-2 text-[11px] text-muted-foreground">
                Stops upgrading at: <strong className="text-foreground">{QUALITY_TIERS.find(t => t.id === draft.cutoffQualityId)?.label ?? draft.cutoffQualityId}</strong>
              </p>
            </div>
          )}

          <div className="space-y-3">
            <div>
              <p className="text-[13px] font-semibold text-foreground">Release scoring preset</p>
              <p className="text-[12px] leading-relaxed text-muted-foreground">
                This controls which release traits get boosted or blocked. It is separate from quality order so users can reason about it cleanly.
              </p>
            </div>
            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
              {CUSTOM_FORMAT_BUNDLES
                .filter((bundle) =>
                  bundle.mediaType === "all"
                    || (draft.mediaType === "tv" && bundle.mediaType === "tv")
                    || (draft.mediaType === "anime" && bundle.mediaType === "tv")
                    || (draft.mediaType === "movies" && bundle.mediaType === "movies")
                )
                .map((bundle) => {
                  const isSelected = draft.formatBundleId === bundle.id;
                  const count = bundle.includes.filter((entry) =>
                    BUNDLED_CUSTOM_FORMATS.some((cf) => cf.trashId === entry.trashId)
                  ).length;

                  return (
                    <button
                      key={bundle.id}
                      type="button"
                      onClick={() => applyFormatBundle(bundle)}
                      className={cn(
                        "rounded-2xl border p-4 text-left transition-all",
                        isSelected
                          ? "border-primary/40 bg-primary/10 shadow-[0_0_0_2px_hsl(var(--primary)/0.16)]"
                          : "border-hairline bg-surface-1 hover:border-primary/25"
                      )}
                    >
                      <div className="flex items-start justify-between gap-3">
                        <div>
                          <p className="font-semibold text-foreground">{bundle.name}</p>
                          <p className="mt-1 text-[11.5px] leading-relaxed text-muted-foreground">{bundle.description}</p>
                        </div>
                        {isSelected ? (
                          <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-primary text-primary-foreground">
                            <Check className="h-3.5 w-3.5" />
                          </span>
                        ) : null}
                      </div>
                      <div className="mt-3 flex flex-wrap items-center gap-2">
                        <span className="rounded-full border border-hairline bg-background/60 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
                          {bundle.level}
                        </span>
                        <span className="rounded-full border border-hairline bg-background/60 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">
                          {count} rules
                        </span>
                      </div>
                    </button>
                  );
                })}
            </div>
          </div>

          {/* Enhancement toggles */}
          <div className="grid gap-3 sm:grid-cols-2">
            {ENHANCEMENTS.map((enh) => {
              const isOn = activeEnhancements.has(enh.id);
              const isNegative = enh.score < 0;
              return (
                <button
                  key={enh.id}
                  type="button"
                  onClick={() => toggleEnhancement(enh)}
                  className={cn(
                    "flex items-start gap-3 rounded-2xl border p-4 text-left transition-all duration-150",
                    isOn
                      ? isNegative
                        ? "border-destructive/25 bg-destructive/5"
                        : "border-primary/30 bg-primary/5"
                      : "border-hairline bg-surface-1 hover:border-primary/20"
                  )}
                >
                  <div
                    className={cn(
                      "mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full border-2 transition-all",
                      isOn
                        ? isNegative
                          ? "border-destructive bg-destructive"
                          : "border-primary bg-primary"
                        : "border-muted-foreground/40"
                    )}
                  >
                    {isOn && <Check className="h-3 w-3 text-white" />}
                  </div>
                  <div className="min-w-0">
                    <p className={cn("text-[13px] font-medium", isOn ? "text-foreground" : "text-foreground/80")}>
                      {enh.label}
                    </p>
                    <p className="mt-0.5 text-[11.5px] text-muted-foreground">{enh.sublabel}</p>
                    <p className={cn("mt-1 text-[10.5px] font-mono font-semibold", isNegative ? "text-destructive/70" : "text-primary/70")}>
                      {isNegative ? `Score: ${enh.score.toLocaleString()}` : `+${enh.score.toLocaleString()} score`}
                    </p>
                  </div>
                </button>
              );
            })}
          </div>

          {/* Upgrade setting */}
          <div className="rounded-2xl border border-hairline bg-surface-1 p-4">
            <div className="flex items-center justify-between gap-3">
              <div>
                <p className="text-[13px] font-medium text-foreground">Auto-upgrade quality</p>
                <p className="text-[12px] text-muted-foreground">
                  When a better version becomes available, automatically download it
                </p>
              </div>
              <button
                type="button"
                onClick={() => setDraft((d) => ({ ...d, upgradeAllowed: !d.upgradeAllowed }))}
                className={cn(
                  "relative inline-flex h-6 w-11 shrink-0 rounded-full border-2 border-transparent transition-colors",
                  draft.upgradeAllowed ? "bg-primary" : "bg-muted-foreground/30"
                )}
              >
                <span
                  className={cn(
                    "pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow-lg transition-transform",
                    draft.upgradeAllowed ? "translate-x-5" : "translate-x-0"
                  )}
                />
              </button>
            </div>
          </div>

          <div className="flex justify-between">
            <Button variant="ghost" onClick={() => setStep("preset")} className="gap-2">
              <ArrowLeft className="h-4 w-4" />
              Back
            </Button>
            <Button onClick={() => setStep("review")} className="gap-2">
              Next — Review
              <ArrowRight className="h-4 w-4" />
            </Button>
          </div>
        </div>
      )}

      {/* ── Step 3: Review & save ── */}
      {step === "review" && (
        <div className="space-y-[var(--page-gap)]">
          <div>
            <h2 className="text-lg font-semibold text-foreground">Review your profile</h2>
            <p className="text-[13px] text-muted-foreground">
              Give it a name and save. You can change everything later.
            </p>
          </div>

          {/* Name input */}
          <div className="space-y-1.5">
            <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">
              Profile name
            </label>
            <Input
              autoFocus
              value={draft.name}
              onChange={(e) => setDraft((d) => ({ ...d, name: e.target.value }))}
              placeholder={preset?.name ?? "My profile"}
              className="h-11 text-base"
            />
          </div>

          {/* Summary */}
          <div className="space-y-[calc(var(--field-group-pad)*0.9)] rounded-2xl border border-hairline bg-surface-1 p-[var(--field-group-pad)]">
            <p className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">Profile summary</p>

            {/* Quality order */}
            <div>
              <p className="mb-2 text-[12.5px] font-medium text-foreground">Quality preference</p>
              <div className="flex flex-wrap gap-2">
                {draft.qualityOrder.map((qId, i) => {
                  const tier = QUALITY_TIERS.find((t) => t.id === qId);
                  return tier ? (
                    <span
                      key={qId}
                      className={cn(
                        "rounded-full border px-2.5 py-1 text-[11px]",
                        i === 0 ? "border-primary/30 bg-primary/10 text-foreground font-semibold" : "border-hairline text-muted-foreground"
                      )}
                    >
                      {i === 0 ? "★ " : ""}{tier.label}
                    </span>
                  ) : null;
                })}
              </div>
            </div>

            {/* Active CFs summary */}
            {draft.activeCFs.size > 0 && (
              <div>
                <p className="mb-2 text-[12.5px] font-medium text-foreground">
                  Active custom formats ({draft.activeCFs.size})
                </p>
                <div className="grid gap-1">
                  {Array.from(draft.activeCFs.entries())
                    .sort(([, a], [, b]) => Math.abs(b) - Math.abs(a))
                    .slice(0, 8)
                    .map(([trashId, score]) => {
                      const cf = BUNDLED_CUSTOM_FORMATS.find((c) => c.trashId === trashId);
                      if (!cf) return null;
                      const isNegative = score < 0;
                      return (
                        <div key={trashId} className="flex items-center justify-between gap-2 rounded-lg px-2 py-1">
                          <span className="text-[12px] text-foreground">{cf.name}</span>
                          <span className={cn(
                            "font-mono text-[10.5px] font-semibold",
                            isNegative ? "text-destructive/70" : score > 100 ? "text-primary/80" : "text-muted-foreground"
                          )}>
                            {score > 0 ? "+" : ""}{score.toLocaleString()}
                          </span>
                        </div>
                      );
                    })}
                  {draft.activeCFs.size > 8 && (
                    <p className="mt-1 text-[11px] text-muted-foreground">
                      +{draft.activeCFs.size - 8} more formats
                    </p>
                  )}
                </div>
              </div>
            )}

            {/* Settings */}
            <div className="grid grid-cols-2 gap-2 text-[12px]">
              <div className="flex items-center gap-2 rounded-xl border border-hairline px-3 py-2">
                <span className="text-muted-foreground">Auto-upgrade</span>
                <span className={cn("ml-auto font-semibold", draft.upgradeAllowed ? "text-success" : "text-muted-foreground")}>
                  {draft.upgradeAllowed ? "On" : "Off"}
                </span>
              </div>
              <div className="flex items-center gap-2 rounded-xl border border-hairline px-3 py-2">
                <span className="text-muted-foreground">Min score</span>
                <span className="ml-auto font-semibold text-foreground">{draft.minFormatScore}</span>
              </div>
            </div>
          </div>

          <div className="flex justify-between">
            <Button variant="ghost" onClick={() => setStep("enhance")} className="gap-2">
              <ArrowLeft className="h-4 w-4" />
              Back
            </Button>
            <div className="flex gap-2">
              <Button variant="ghost" onClick={onCancel}>Cancel</Button>
              <Button onClick={() => void handleSave()} disabled={saving || !draft.name.trim()} className="gap-2">
                {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                {saving ? "Saving…" : "Create profile"}
              </Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

/* ── Helpers ─────────────────────────────────────────────────────── */
const steps: Step[] = ["preset", "enhance", "review"];
const STEP_LABELS: Record<Step, string> = {
  preset: "Choose preset",
  enhance: "Enhancements",
  review: "Review & save",
};

function ChevronRight({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 16 16" fill="none">
      <path d="M6 4l4 4-4 4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
