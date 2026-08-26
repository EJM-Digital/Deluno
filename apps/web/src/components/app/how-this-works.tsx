/**
 * The explainer that sits above an area and says how its parts fit together.
 *
 * It exists for the question a screenshot cannot answer: not "what is this
 * control", which the control should say itself, but "why are there four tabs
 * here and which one do I touch first". So the lead states the shape of the
 * area in one breath, and the steps are the order things actually happen in.
 *
 * Rules it keeps, so thirteen of these do not read as thirteen different apps:
 *
 * - **Never restate the page title.** The toolbar already said where you are.
 * - **Order, not inventory.** Steps are a sequence — first this, then that —
 *   never a list of the tabs above, which would say the same thing twice.
 * - **Two to four steps.** More than four is a manual, and belongs in docs.
 * - **Name the thing the user will see**, in the words the UI uses for it.
 */
import type { ReactNode } from "react";
import { Info } from "lucide-react";
import { cn } from "../../lib/utils";

export interface HowThisWorksStep {
  /** Numbered for you — write the title without "1." in front. */
  title: string;
  body: ReactNode;
}

export function HowThisWorks({
  id,
  lead,
  steps,
  className
}: {
  /** Unique per page; used to label the region for screen readers. */
  id: string;
  lead: ReactNode;
  steps: readonly HowThisWorksStep[];
  className?: string;
}) {
  const titleId = `${id}-how-this-works`;

  return (
    <section aria-labelledby={titleId} className={cn("mb-4 overflow-hidden rounded-2xl border border-info/20 bg-info/[0.04]", className)}>
      <div className="flex gap-3 px-[var(--card-pad-x)] py-3">
        <span aria-hidden className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-info/25 bg-info/10 text-info">
          <Info className="h-4 w-4" />
        </span>
        <div className="min-w-0">
          <h2 id={titleId} className="text-[length:var(--type-card-title)] font-semibold text-foreground">How this works</h2>
          <p className="mt-1 max-w-4xl text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{lead}</p>
        </div>
      </div>
      {steps.length > 0 ? (
        <ol
          className={cn(
            "grid border-t border-info/15",
            steps.length === 2 ? "md:grid-cols-2" : steps.length === 4 ? "md:grid-cols-4" : "md:grid-cols-3"
          )}
        >
          {steps.map((step, index) => (
            <li
              key={step.title}
              className={cn(
                "border-info/15 px-[var(--card-pad-x)] py-3",
                index < steps.length - 1 && "md:border-r"
              )}
            >
              <p className="text-[length:var(--type-caption)] font-semibold text-foreground">
                {index + 1}. {step.title}
              </p>
              <p className="mt-1 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{step.body}</p>
            </li>
          ))}
        </ol>
      ) : null}
    </section>
  );
}
