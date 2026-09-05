/**
 * What Deluno does when an import fails — and the right to disagree with it.
 *
 * <p>DESIGN-007, James on all sixteen decisions at once: <i>"I think all these
 * things we decided need to have configuration toggles to set them on and off
 * in a management / blocklist console."</i> The right harshness depends on the
 * library. Somebody on a fast line with spare disk wants it strict; somebody on
 * a flaky share does not, and Deluno refusing releases on their behalf is how a
 * blocklist fills with things that were never the file's fault.</p>
 *
 * <p>Two things the screen has to get right. It is grouped by whose fault the
 * failure was, so it reads as the argument rather than as an alphabet of
 * seventeen codes; and every row shows what Deluno ships with, so "back to
 * default" means something specific rather than something to be guessed at.</p>
 *
 * Contract: GET /api/failure-rules, PUT and DELETE /api/failure-rules/{code}.
 */
import { useState } from "react";
import { RotateCcw } from "lucide-react";
import {
  DECISION_OPTIONS,
  FAILURE_CATEGORIES,
  decisionWords,
  reasonWords,
  type BlockDecision,
  type ImportFailureRule
} from "../../lib/failure-reasons";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { Button } from "../ui/button";
import { SegmentedControl } from "../ui/segmented-control";

export interface FailureRulesConsoleProps {
  rules: ImportFailureRule[];
  onChanged: () => void;
}

export function FailureRulesConsole({ rules, onChanged }: FailureRulesConsoleProps) {
  const [busy, setBusy] = useState<string | null>(null);

  async function save(rule: ImportFailureRule, decision: BlockDecision) {
    setBusy(rule.reasonCode);
    try {
      // Choosing the shipped answer again is a reset, not a setting. Writing it
      // down would freeze it — a later change to what Deluno ships with would
      // never reach anybody who had ever pressed this.
      const backToDefault = decision === rule.defaultDecision;
      const response = await authedFetch(
        `/api/failure-rules/${rule.reasonCode}`,
        backToDefault
          ? { method: "DELETE" }
          : {
              method: "PUT",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ decision })
            }
      );
      if (!response.ok) throw new Error("rule-failed");

      toast.success(`${reasonWords(rule.reasonCode)} — Deluno now ${decisionWords(decision)}.`);
      onChanged();
    } catch {
      toast.error("That rule could not be changed.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="grid gap-[var(--grid-gap)]">
      {FAILURE_CATEGORIES.map((category) => {
        const inCategory = rules.filter((rule) => rule.category === category.id);
        if (!inCategory.length) return null;

        return (
          <section key={category.id} className="grid gap-2">
            <div>
              <h3 className="text-[length:var(--type-body-sm)] font-medium text-foreground">{category.title}</h3>
              <p className="text-[length:var(--type-caption)] text-muted-foreground">{category.blurb}</p>
            </div>

            <div className="grid gap-2">
              {inCategory.map((rule) => (
                <div
                  key={rule.reasonCode}
                  className="grid items-center gap-2 rounded-[10px] border border-hairline bg-surface-1 px-[var(--field-pad-x)] py-2 sm:grid-cols-[minmax(0,1fr)_auto]"
                >
                  <div className="min-w-0">
                    <p className="truncate text-[length:var(--type-body-sm)] text-foreground">
                      {reasonWords(rule.reasonCode)}
                    </p>
                    <p className="text-[length:var(--type-caption)] text-muted-foreground">
                      {rule.isOverridden ? `Deluno ships with "${labelFor(rule.defaultDecision)}"` : "Deluno's own answer"}
                    </p>
                  </div>
                  <div className="flex items-center gap-2 sm:w-[22rem]">
                    <SegmentedControl<BlockDecision>
                      aria-label={`What Deluno does when: ${reasonWords(rule.reasonCode)}`}
                      value={rule.decision}
                      disabled={busy !== null}
                      onValueChange={(decision) => void save(rule, decision)}
                      options={DECISION_OPTIONS}
                    />
                    {rule.isOverridden ? (
                      <Button
                        type="button"
                        size="sm"
                        variant="ghost"
                        aria-label={`Put ${reasonWords(rule.reasonCode)} back to Deluno's answer`}
                        disabled={busy !== null}
                        onClick={() => void save(rule, rule.defaultDecision)}
                      >
                        <RotateCcw aria-hidden className="h-3.5 w-3.5" />
                      </Button>
                    ) : null}
                  </div>
                </div>
              ))}
            </div>
          </section>
        );
      })}
    </div>
  );
}

function labelFor(decision: BlockDecision): string {
  return DECISION_OPTIONS.find((option) => option.value === decision)?.label ?? decision;
}
