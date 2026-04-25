/**
 * Custom Format Library Browser
 *
 * 80+ pre-built TRaSH Guide custom formats organised by category with
 * plain-English descriptions and one-click add. No regex knowledge required.
 */

import { useState, useMemo } from "react";
import {
  Check,
  CircleMinus,
  CirclePlus,
  Info,
  Search,
  X,
} from "lucide-react";
import {
  BUNDLED_CUSTOM_FORMATS,
  CF_CATEGORY_META,
  CF_CATEGORY_ORDER,
  type BundledCF,
  type CFCategory,
} from "../../lib/trash-guide-data";
import { Input } from "../ui/input";
import { cn } from "../../lib/utils";

/* ── Types ───────────────────────────────────────────────────────── */
export interface CFLibrarySelection {
  trashId: string;
  score: number;
}

interface Props {
  /** Currently active selections (trashId → score) */
  selections: Map<string, number>;
  onAdd: (cf: BundledCF, score: number) => void;
  onRemove: (trashId: string) => void;
  onScoreChange: (trashId: string, score: number) => void;
}

/* ── Category colour pills ───────────────────────────────────────── */
const CATEGORY_BG: Record<CFCategory, string> = {
  hdr:       "bg-violet-500/10 text-violet-400 border-violet-500/20",
  codec:     "bg-blue-500/10 text-blue-400 border-blue-500/20",
  audio:     "bg-green-500/10 text-green-400 border-green-500/20",
  channels:  "bg-teal-500/10 text-teal-400 border-teal-500/20",
  source:    "bg-amber-500/10 text-amber-400 border-amber-500/20",
  streaming: "bg-sky-500/10 text-sky-400 border-sky-500/20",
  edition:   "bg-orange-500/10 text-orange-400 border-orange-500/20",
  groups:    "bg-emerald-500/10 text-emerald-400 border-emerald-500/20",
  anime:     "bg-pink-500/10 text-pink-400 border-pink-500/20",
  language:  "bg-cyan-500/10 text-cyan-400 border-cyan-500/20",
  unwanted:  "bg-red-500/10 text-red-400 border-red-500/20",
  misc:      "bg-muted/30 text-muted-foreground border-hairline",
};

/* ── Score display helper ────────────────────────────────────────── */
function scoreLabel(score: number) {
  if (score <= -10000) return "Blocked";
  if (score > 0) return `+${score.toLocaleString()}`;
  return score.toLocaleString();
}

function scoreColor(score: number) {
  if (score <= -10000) return "text-destructive";
  if (score > 500) return "text-primary";
  if (score > 0) return "text-muted-foreground";
  return "text-muted-foreground/50";
}

/* ── CF card ─────────────────────────────────────────────────────── */
function CFCard({
  cf,
  score,
  isAdded,
  onAdd,
  onRemove,
  onScoreChange,
}: {
  cf: BundledCF;
  score: number | undefined;
  isAdded: boolean;
  onAdd: () => void;
  onRemove: () => void;
  onScoreChange: (s: number) => void;
}) {
  const [showDetail, setShowDetail] = useState(false);
  const [scoreInput, setScoreInput] = useState<string>(() =>
    isAdded && score !== undefined ? String(score) : String(cf.defaultScore)
  );

  const effectiveScore = isAdded && score !== undefined ? score : cf.defaultScore;

  return (
    <div
      className={cn(
        "group relative rounded-2xl border transition-all duration-200",
        isAdded
          ? effectiveScore <= -10000
            ? "border-destructive/25 bg-destructive/5"
            : "border-primary/25 bg-primary/5"
          : "border-hairline bg-surface-1 hover:border-primary/20 hover:shadow-sm"
      )}
    >
      <div className="flex items-start gap-3 p-4">
        {/* Category dot */}
        <div className={cn("mt-0.5 flex shrink-0 items-center justify-center rounded-full border px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide", CATEGORY_BG[cf.category])}>
          {CF_CATEGORY_META[cf.category].label.split(" ")[0]}
        </div>

        {/* Content */}
        <div className="min-w-0 flex-1">
          <div className="flex items-start justify-between gap-2">
            <div>
              <p className="text-[13px] font-semibold text-foreground leading-tight">{cf.name}</p>
              <p className="mt-0.5 text-[11.5px] leading-relaxed text-muted-foreground line-clamp-2">{cf.description}</p>
            </div>
            <div className={cn("shrink-0 text-right")}>
              <span className={cn("font-mono text-[11px] font-bold", scoreColor(effectiveScore))}>
                {scoreLabel(effectiveScore)}
              </span>
            </div>
          </div>

          {/* Score editor when added */}
          {isAdded && (
            <div className="mt-3 flex items-center gap-2">
              <span className="text-[11px] text-muted-foreground">Score:</span>
              <input
                type="number"
                value={scoreInput}
                onChange={(e) => {
                  setScoreInput(e.target.value);
                  const n = Number(e.target.value);
                  if (!isNaN(n)) onScoreChange(n);
                }}
                className="h-7 w-24 rounded-lg border border-hairline bg-background px-2 font-mono text-[12px] text-foreground focus:border-primary/50 focus:outline-none focus:ring-2 focus:ring-primary/20"
              />
              <button
                type="button"
                onClick={() => {
                  setScoreInput(String(cf.defaultScore));
                  onScoreChange(cf.defaultScore);
                }}
                className="text-[10.5px] text-muted-foreground hover:text-foreground underline underline-offset-2"
              >
                reset
              </button>
            </div>
          )}

          {/* Patterns (expandable) */}
          {showDetail && (
            <div className="mt-3 rounded-xl border border-hairline bg-background/50 p-3">
              <p className="mb-1.5 text-[10px] font-bold uppercase tracking-wider text-muted-foreground">Match patterns</p>
              <div className="space-y-1">
                {cf.patterns.map((p) => (
                  <code key={p} className="block text-[10.5px] text-muted-foreground break-all">
                    {p}
                  </code>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>

      {/* Actions */}
      <div className="flex items-center gap-1.5 border-t border-hairline/60 px-4 py-2">
        <button
          type="button"
          onClick={() => setShowDetail((v) => !v)}
          className="flex items-center gap-1 text-[11px] text-muted-foreground hover:text-foreground"
        >
          <Info className="h-3 w-3" />
          {showDetail ? "Hide patterns" : "Show patterns"}
        </button>
        <div className="ml-auto">
          {isAdded ? (
            <button
              type="button"
              onClick={onRemove}
              className="flex items-center gap-1.5 rounded-lg border border-destructive/30 bg-destructive/5 px-2.5 py-1 text-[11.5px] font-medium text-destructive hover:bg-destructive/10 transition-colors"
            >
              <CircleMinus className="h-3.5 w-3.5" />
              Remove
            </button>
          ) : (
            <button
              type="button"
              onClick={onAdd}
              className="flex items-center gap-1.5 rounded-lg border border-primary/30 bg-primary/5 px-2.5 py-1 text-[11.5px] font-medium text-foreground hover:bg-primary/10 transition-colors"
            >
              <CirclePlus className="h-3.5 w-3.5 text-primary" />
              Add to profile
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

/* ── Main browser ─────────────────────────────────────────────────── */
export function CFLibraryBrowser({ selections, onAdd, onRemove, onScoreChange }: Props) {
  const [query, setQuery] = useState("");
  const [activeCategory, setActiveCategory] = useState<CFCategory | "all" | "active">("all");

  const filteredCFs = useMemo(() => {
    let list = BUNDLED_CUSTOM_FORMATS;

    if (activeCategory === "active") {
      list = list.filter((cf) => selections.has(cf.trashId));
    } else if (activeCategory !== "all") {
      list = list.filter((cf) => cf.category === activeCategory);
    }

    if (query.trim()) {
      const q = query.toLowerCase();
      list = list.filter(
        (cf) =>
          cf.name.toLowerCase().includes(q) ||
          cf.description.toLowerCase().includes(q) ||
          cf.category.toLowerCase().includes(q)
      );
    }

    return list;
  }, [query, activeCategory, selections]);

  const categoryCounts = useMemo(() => {
    const counts: Record<string, number> = { all: BUNDLED_CUSTOM_FORMATS.length, active: selections.size };
    for (const cf of BUNDLED_CUSTOM_FORMATS) {
      counts[cf.category] = (counts[cf.category] ?? 0) + 1;
    }
    return counts;
  }, [selections]);

  return (
    <div className="flex flex-col gap-4">
      {/* Search */}
      <div className="relative">
        <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search custom formats…"
          className="h-10 pl-9 pr-9"
        />
        {query && (
          <button
            type="button"
            onClick={() => setQuery("")}
            className="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
          >
            <X className="h-4 w-4" />
          </button>
        )}
      </div>

      {/* Category tabs — horizontal scroll */}
      <div className="-mx-1 flex gap-1.5 overflow-x-auto px-1 pb-1">
        {(["all", "active", ...CF_CATEGORY_ORDER] as const).map((cat) => {
          const count = categoryCounts[cat] ?? 0;
          const isActive = activeCategory === cat;
          const meta = cat !== "all" && cat !== "active" ? CF_CATEGORY_META[cat] : null;

          return (
            <button
              key={cat}
              type="button"
              onClick={() => setActiveCategory(cat)}
              className={cn(
                "flex shrink-0 items-center gap-1.5 rounded-full border px-3 py-1.5 text-[11.5px] font-medium transition-all whitespace-nowrap",
                isActive
                  ? "border-primary/40 bg-primary/10 text-foreground"
                  : "border-hairline text-muted-foreground hover:border-primary/20 hover:text-foreground"
              )}
            >
              {cat === "active" && selections.size > 0 && (
                <span className="flex h-4 w-4 items-center justify-center rounded-full bg-primary text-[9px] font-bold text-primary-foreground">
                  {selections.size}
                </span>
              )}
              {cat === "all" ? "All formats" : cat === "active" ? "Active" : meta?.label ?? cat}
              {count > 0 && cat !== "active" && (
                <span className="text-[10px] text-muted-foreground/60">({count})</span>
              )}
            </button>
          );
        })}
      </div>

      {/* Category description */}
      {activeCategory !== "all" && activeCategory !== "active" && (
        <div className="flex items-center gap-2 rounded-xl border border-hairline bg-surface-1 px-3 py-2">
          <Info className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <p className="text-[12px] text-muted-foreground">{CF_CATEGORY_META[activeCategory].description}</p>
        </div>
      )}

      {/* CF grid */}
      {filteredCFs.length > 0 ? (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {filteredCFs.map((cf) => (
            <CFCard
              key={cf.trashId}
              cf={cf}
              score={selections.get(cf.trashId)}
              isAdded={selections.has(cf.trashId)}
              onAdd={() => onAdd(cf, cf.defaultScore)}
              onRemove={() => onRemove(cf.trashId)}
              onScoreChange={(s) => onScoreChange(cf.trashId, s)}
            />
          ))}
        </div>
      ) : (
        <div className="flex flex-col items-center justify-center gap-3 py-12 text-center">
          <Search className="h-8 w-8 text-muted-foreground/30" />
          <p className="text-[13px] text-muted-foreground">No formats match your search</p>
        </div>
      )}
    </div>
  );
}
