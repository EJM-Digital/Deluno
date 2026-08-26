/**
 * The explainer that sits above a configuration area and says how its parts
 * fit together.
 *
 * It answers the question a screenshot cannot: not "what is this control",
 * which the control should say itself, but "why are there four tabs here and
 * which one do I touch first". So the lead states the shape of the area in one
 * breath, and the steps are the order things actually happen in.
 *
 * **It collapses, and the choice is one preference, not one per area.** An
 * explainer is furniture for someone who read it once; seven of them, always
 * open, is the same thing said seven times and a chunk of every pane spent on
 * it. Collapse any one and they all stay collapsed until you open one again.
 * A first-run install sees them open.
 *
 * **It is deliberately not tinted.** Hue in this app belongs to state (#290),
 * and an explainer is not a state — it is the same on the day everything is
 * broken as on the day nothing is.
 *
 * Rules the copy keeps, so seven of these do not read as seven different apps:
 *
 * - **Never restate the page title.** The toolbar already said where you are.
 * - **Order, not inventory.** Steps are a sequence — first this, then that —
 *   never a list of the tabs above, which would say the same thing twice.
 * - **Two to four steps.** More than four is a manual, and belongs in docs.
 * - **Name the thing the user will see**, in the words the UI uses for it.
 */
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { ChevronDown } from "lucide-react";
import { cn } from "../../lib/utils";

const STORAGE_KEY = "deluno-how-this-works";
const CHANGE_EVENT = "deluno-how-this-works-change";

function readCollapsed() {
  try {
    return window.localStorage.getItem(STORAGE_KEY) === "collapsed";
  } catch {
    // Private windows and blocked site data both throw here. An explainer that
    // cannot remember your choice is a small loss; one that crashes the page is
    // not acceptable.
    return false;
  }
}

/**
 * One answer for every area. Panels listen for each other's changes so
 * collapsing one does not leave the next page you open still expanded.
 */
function useCollapsedPreference(): [boolean, (next: boolean) => void] {
  const [collapsed, setCollapsed] = useState(readCollapsed);

  useEffect(() => {
    const sync = () => setCollapsed(readCollapsed());
    window.addEventListener(CHANGE_EVENT, sync);
    window.addEventListener("storage", sync);
    return () => {
      window.removeEventListener(CHANGE_EVENT, sync);
      window.removeEventListener("storage", sync);
    };
  }, []);

  const update = useCallback((next: boolean) => {
    setCollapsed(next);
    try {
      window.localStorage.setItem(STORAGE_KEY, next ? "collapsed" : "expanded");
    } catch {
      // Remembering is the nice-to-have; collapsing right now still worked.
    }
    window.dispatchEvent(new Event(CHANGE_EVENT));
  }, []);

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
  /** Unique per page; used to label the region for screen readers. */
  id: string;
  lead: ReactNode;
  steps: readonly HowThisWorksStep[];
  className?: string;
}) {
  const [collapsed, setCollapsed] = useCollapsedPreference();
  const titleId = `${id}-how-this-works`;
  const bodyId = `${id}-how-this-works-body`;

  return (
    <section aria-labelledby={titleId} className={cn("mb-4 overflow-hidden rounded-2xl border border-hairline bg-surface-2/40", className)}>
      <h2 id={titleId}>
        <button
          type="button"
          aria-expanded={!collapsed}
          aria-controls={bodyId}
          onClick={() => setCollapsed(!collapsed)}
          className="flex w-full items-center gap-2 px-[var(--card-pad-x)] py-3 text-left transition-colors hover:bg-surface-2/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <ChevronDown aria-hidden className={cn("h-4 w-4 shrink-0 text-muted-foreground transition-transform duration-200", collapsed && "-rotate-90")} />
          <span className="text-[length:var(--type-card-title)] font-semibold text-foreground">How this works</span>
          {collapsed ? <span className="truncate text-[length:var(--type-caption)] text-muted-foreground">— hidden on every area until you open one</span> : null}
        </button>
      </h2>
      <div id={bodyId} hidden={collapsed}>
        <p className="max-w-4xl px-[var(--card-pad-x)] pb-3 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{lead}</p>
        {steps.length > 0 ? (
          <ol
            className={cn(
              "grid border-t border-hairline",
              steps.length === 2 ? "md:grid-cols-2" : steps.length === 4 ? "md:grid-cols-4" : "md:grid-cols-3"
            )}
          >
            {steps.map((step, index) => (
              <li
                key={step.title}
                className={cn(
                  "border-hairline px-[var(--card-pad-x)] py-3",
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
