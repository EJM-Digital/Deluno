/**
 * The explainer that sits above a configuration area and says how its parts
 * fit together.
 *
 * It answers the question a screenshot cannot: not "what is this control",
 * which the control should say itself, but "why are there four tabs here and
 * which one do I touch first". So the lead states the shape of the area in one
 * breath, and the steps are the order things actually happen in.
 *
 * **It starts collapsed, and each area remembers its own answer.** An explainer
 * is furniture for someone who read it once, so it asks for a click rather than
 * a chunk of every pane. Open Media Management and Quality & Release stays shut:
 * knowing how one area fits together says nothing about needing the next.
 *
 * Rules the copy keeps, so seven of these do not read as seven different apps:
 *
 * - **Never restate the page title.** The toolbar already said where you are.
 * - **Order, not inventory.** Steps are a sequence — first this, then that —
 *   never a list of the tabs above, which would say the same thing twice.
 * - **Two to four steps.** More than four is a manual, and belongs in docs.
 * - **Name the thing the user will see**, in the words the UI uses for it.
 */
import { useCallback, useState, type ReactNode } from "react";
import { ChevronDown, Info } from "lucide-react";
import { cn } from "../../lib/utils";

const storageKey = (id: string) => `deluno-how-this-works:${id}`;

function readCollapsed(id: string) {
  try {
    return window.localStorage.getItem(storageKey(id)) !== "expanded";
  } catch {
    // Private windows and blocked site data both throw here. An explainer that
    // cannot remember your choice is a small loss; one that crashes the page is
    // not acceptable.
    return true;
  }
}

function useCollapsedPreference(id: string): [boolean, (next: boolean) => void] {
  const [collapsed, setCollapsed] = useState(() => readCollapsed(id));

  const update = useCallback(
    (next: boolean) => {
      setCollapsed(next);
      try {
        window.localStorage.setItem(storageKey(id), next ? "collapsed" : "expanded");
      } catch {
        // Remembering is the nice-to-have; opening it right now still worked.
      }
    },
    [id]
  );

  return [collapsed, update];
}

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
  /** Unique per page; labels the region and keys its open/closed memory. */
  id: string;
  lead: ReactNode;
  steps: readonly HowThisWorksStep[];
  className?: string;
}) {
  const [collapsed, setCollapsed] = useCollapsedPreference(id);
  const titleId = `${id}-how-this-works`;
  const bodyId = `${id}-how-this-works-body`;

  return (
    <section aria-labelledby={titleId} className={cn("mb-4 overflow-hidden rounded-2xl border border-info/20 bg-info/[0.04]", className)}>
      <h2 id={titleId}>
        <button
          type="button"
          aria-expanded={!collapsed}
          aria-controls={bodyId}
          onClick={() => setCollapsed(!collapsed)}
          className="flex w-full items-center gap-3 px-[var(--card-pad-x)] py-3 text-left transition-colors hover:bg-info/[0.08] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {/* Nothing without aria-hidden may go in here: the button is the
              heading's content, so anything readable becomes part of the
              heading's accessible name. State is carried by aria-expanded. */}
          <span aria-hidden className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full border border-info/25 bg-info/10 text-info">
            <Info className="h-4 w-4" />
          </span>
          <span className="text-[length:var(--type-card-title)] font-semibold text-foreground">How this works</span>
          <ChevronDown aria-hidden className={cn("ml-auto h-4 w-4 shrink-0 text-muted-foreground transition-transform duration-200", collapsed && "-rotate-90")} />
        </button>
      </h2>
      <div id={bodyId} hidden={collapsed}>
        <p className="max-w-4xl px-[var(--card-pad-x)] pb-3 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{lead}</p>
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
      </div>
    </section>
  );
}
