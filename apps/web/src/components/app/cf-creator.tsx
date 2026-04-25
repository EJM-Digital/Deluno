/**
 * Simplified Custom Format Creator
 *
 * Builds a custom format from plain text. No regex knowledge required.
 * For power users, a toggle reveals the raw regex editor.
 */

import { useState } from "react";
import {
  AlertTriangle,
  ChevronDown,
  ChevronUp,
  Code2,
  HelpCircle,
  Loader2,
  Plus,
  Save,
  Trash2,
  X,
} from "lucide-react";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { cn } from "../../lib/utils";
import { CF_CATEGORY_META, CF_CATEGORY_ORDER, type CFCategory } from "../../lib/trash-guide-data";

/* ── Types ───────────────────────────────────────────────────────── */

export type MatchMode = "contains" | "starts-with" | "ends-with" | "exact" | "regex";

export interface CFCondition {
  id: string;
  mode: MatchMode;
  value: string;
  /** Negate the match — useful for "does NOT contain" */
  negate: boolean;
}

export interface CFDraft {
  name: string;
  category: CFCategory;
  conditions: CFCondition[];
}

interface Props {
  onSave: (draft: CFDraft) => Promise<void>;
  onCancel: () => void;
  initial?: Partial<CFDraft>;
}

/* ── Match mode labels ───────────────────────────────────────────── */

const MATCH_MODES: { id: MatchMode; label: string; example: string }[] = [
  { id: "contains",    label: "Contains",    example: "e.g. REMUX" },
  { id: "starts-with", label: "Starts with", example: "e.g. BluRay" },
  { id: "ends-with",   label: "Ends with",   example: "e.g. .mkv" },
  { id: "exact",       label: "Exact match", example: "e.g. IMAX" },
  { id: "regex",       label: "Regex",       example: "e.g. \\bREMUX\\b" },
];

function conditionToRegex(c: CFCondition): string {
  if (c.mode === "regex") return c.value;
  const escaped = c.value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  switch (c.mode) {
    case "contains":    return `\\b${escaped}\\b`;
    case "starts-with": return `^${escaped}`;
    case "ends-with":   return `${escaped}$`;
    case "exact":       return `^${escaped}$`;
  }
}

let _condId = 0;
function newCondId() { return `cond-${++_condId}`; }

function emptyCondition(): CFCondition {
  return { id: newCondId(), mode: "contains", value: "", negate: false };
}

/* ── Preview badge ───────────────────────────────────────────────── */
function RegexPreview({ condition }: { condition: CFCondition }) {
  if (!condition.value.trim()) return null;
  const regex = conditionToRegex(condition);
  return (
    <div className="mt-1.5 flex items-center gap-1.5 rounded-lg bg-muted/30 px-2.5 py-1.5">
      <Code2 className="h-3 w-3 shrink-0 text-muted-foreground" />
      <code className="text-[10.5px] text-muted-foreground break-all">
        {condition.negate ? "NOT " : ""}{regex}
      </code>
    </div>
  );
}

/* ── Main component ──────────────────────────────────────────────── */
export function CFCreator({ onSave, onCancel, initial }: Props) {
  const [draft, setDraft] = useState<CFDraft>({
    name: initial?.name ?? "",
    category: initial?.category ?? "misc",
    conditions: initial?.conditions ?? [emptyCondition()],
  });
  const [saving, setSaving] = useState(false);
  const [showAdvanced, setShowAdvanced] = useState(false);

  function updateCondition(id: string, patch: Partial<CFCondition>) {
    setDraft((d) => ({
      ...d,
      conditions: d.conditions.map((c) => (c.id === id ? { ...c, ...patch } : c)),
    }));
  }

  function addCondition() {
    setDraft((d) => ({ ...d, conditions: [...d.conditions, emptyCondition()] }));
  }

  function removeCondition(id: string) {
    setDraft((d) => ({
      ...d,
      conditions: d.conditions.filter((c) => c.id !== id),
    }));
  }

  const canSave =
    draft.name.trim() !== "" &&
    draft.conditions.length > 0 &&
    draft.conditions.every((c) => c.value.trim() !== "");

  async function handleSave() {
    if (!canSave) return;
    setSaving(true);
    try {
      await onSave(draft);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="flex flex-col gap-5">
      {/* Name */}
      <div className="space-y-1.5">
        <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">
          Format name <span className="text-destructive">*</span>
        </label>
        <Input
          autoFocus
          value={draft.name}
          onChange={(e) => setDraft((d) => ({ ...d, name: e.target.value }))}
          placeholder="e.g. My Preferred Groups"
          className="h-10"
        />
      </div>

      {/* Category */}
      <div className="space-y-1.5">
        <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">
          Category
        </label>
        <div className="flex flex-wrap gap-1.5">
          {CF_CATEGORY_ORDER.map((cat) => {
            const meta = CF_CATEGORY_META[cat];
            return (
              <button
                key={cat}
                type="button"
                onClick={() => setDraft((d) => ({ ...d, category: cat }))}
                className={cn(
                  "rounded-full border px-3 py-1.5 text-[11.5px] font-medium transition-all",
                  draft.category === cat
                    ? "border-primary/40 bg-primary/10 text-foreground"
                    : "border-hairline text-muted-foreground hover:border-primary/20"
                )}
              >
                {meta.label}
              </button>
            );
          })}
        </div>
      </div>

      {/* Conditions */}
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <label className="text-[11px] font-bold uppercase tracking-[0.14em] text-muted-foreground">
            Match conditions
          </label>
          <div className="flex items-center gap-1 text-[11px] text-muted-foreground">
            <HelpCircle className="h-3 w-3" />
            A release matches if <strong className="text-foreground px-0.5">any</strong> condition is true
          </div>
        </div>

        <div className="space-y-2.5">
          {draft.conditions.map((cond, i) => (
            <div
              key={cond.id}
              className="rounded-2xl border border-hairline bg-surface-1 p-4 space-y-3"
            >
              <div className="flex items-center gap-2">
                <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-muted/50 text-[10px] font-bold text-muted-foreground">
                  {i + 1}
                </span>
                <p className="text-[12px] font-medium text-foreground">
                  {cond.negate ? "Does NOT match:" : "Matches when release name:"}
                </p>
                {draft.conditions.length > 1 && (
                  <button
                    type="button"
                    onClick={() => removeCondition(cond.id)}
                    className="ml-auto text-muted-foreground/60 hover:text-destructive"
                  >
                    <X className="h-4 w-4" />
                  </button>
                )}
              </div>

              {/* Mode select */}
              <div className="flex flex-wrap gap-1.5">
                {MATCH_MODES.filter((m) => showAdvanced || m.id !== "regex").map((mode) => (
                  <button
                    key={mode.id}
                    type="button"
                    onClick={() => updateCondition(cond.id, { mode: mode.id })}
                    className={cn(
                      "rounded-full border px-2.5 py-1 text-[11px] font-medium transition-all",
                      cond.mode === mode.id
                        ? "border-primary/30 bg-primary/10 text-foreground"
                        : "border-hairline text-muted-foreground hover:border-primary/20"
                    )}
                  >
                    {mode.label}
                  </button>
                ))}
              </div>

              {/* Value input */}
              <div>
                <Input
                  value={cond.value}
                  onChange={(e) => updateCondition(cond.id, { value: e.target.value })}
                  placeholder={MATCH_MODES.find((m) => m.id === cond.mode)?.example ?? "Enter text…"}
                  className={cn("h-10 font-mono", cond.mode === "regex" && "text-[12px]")}
                />
                {showAdvanced && <RegexPreview condition={cond} />}
              </div>

              {/* Negate toggle */}
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => updateCondition(cond.id, { negate: !cond.negate })}
                  className={cn(
                    "flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] font-medium transition-all",
                    cond.negate
                      ? "border-destructive/30 bg-destructive/5 text-destructive"
                      : "border-hairline text-muted-foreground hover:border-primary/20"
                  )}
                >
                  <AlertTriangle className="h-3 w-3" />
                  {cond.negate ? "Is negated (must NOT match)" : "Negate (must NOT match)"}
                </button>
              </div>
            </div>
          ))}
        </div>

        <button
          type="button"
          onClick={addCondition}
          className="flex w-full items-center justify-center gap-2 rounded-xl border-2 border-dashed border-hairline py-3 text-[12.5px] text-muted-foreground transition-all hover:border-primary/25 hover:text-foreground"
        >
          <Plus className="h-4 w-4" />
          Add another condition (OR)
        </button>
      </div>

      {/* Advanced toggle */}
      <button
        type="button"
        onClick={() => setShowAdvanced((v) => !v)}
        className="flex items-center gap-1.5 text-[11.5px] text-muted-foreground hover:text-foreground self-start"
      >
        <Code2 className="h-3.5 w-3.5" />
        {showAdvanced ? "Hide" : "Show"} advanced options (regex mode)
        {showAdvanced ? <ChevronUp className="h-3 w-3" /> : <ChevronDown className="h-3 w-3" />}
      </button>

      {showAdvanced && (
        <div className="rounded-xl border border-amber-500/20 bg-amber-500/5 p-3">
          <p className="text-[12px] text-amber-400/90">
            <strong>Regex mode</strong> — switch any condition to "Regex" to write raw regular expressions.
            Patterns are matched case-insensitively against the full release name. Use <code className="font-mono">\b</code> for word boundaries.
          </p>
        </div>
      )}

      {/* Actions */}
      <div className="flex justify-between border-t border-hairline pt-4">
        <Button variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button onClick={() => void handleSave()} disabled={!canSave || saving} className="gap-2">
          {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
          {saving ? "Saving…" : "Create format"}
        </Button>
      </div>
    </div>
  );
}
