/**
 * First-run onboarding banner.
 *
 * Detects the "fresh install" state (no indexers, no download clients,
 * no libraries) and shows a compact setup checklist linking directly
 * into the relevant settings pages. Dismissible by the user; the
 * dismissed flag is stored in localStorage so it never nags after the
 * user explicitly chooses to hide it, even if they later delete their
 * libraries.
 *
 * Drop into the dashboard's header region; renders nothing once the
 * user has either completed all steps or dismissed the banner.
 */

import { Link } from "react-router-dom";
import { ArrowRight, CheckCircle2, Circle, Sparkles, X } from "lucide-react";
import { useEffect, useState } from "react";
import { cn } from "../../lib/utils";

export interface OnboardingState {
  hasIndexer: boolean;
  hasDownloadClient: boolean;
  hasLibrary: boolean;
}

const DISMISS_KEY = "deluno-onboarding-dismissed";

export function OnboardingBanner({ state }: { state: OnboardingState }) {
  const [dismissed, setDismissed] = useState(false);

  useEffect(() => {
    try {
      setDismissed(window.localStorage.getItem(DISMISS_KEY) === "1");
    } catch {
      /* noop */
    }
  }, []);

  const allDone = state.hasIndexer && state.hasDownloadClient && state.hasLibrary;
  if (allDone || dismissed) return null;

  const steps: { label: string; to: string; done: boolean; hint: string }[] = [
    {
      label: "Choose folders",
      hint: "Where media and completed downloads live.",
      to: "/setup-guide",
      done: state.hasLibrary
    },
    {
      label: "Set quality",
      hint: "Simple quality and release rules.",
      to: "/setup-guide",
      done: state.hasLibrary
    },
    {
      label: "Add an indexer",
      hint: "Providers Deluno queries for releases.",
      to: "/indexers",
      done: state.hasIndexer
    },
    {
      label: "Add a download client",
      hint: "Where approved releases will be sent.",
      to: "/indexers",
      done: state.hasDownloadClient
    },
    {
      label: "Add your first title",
      hint: "A movie or show to start monitoring.",
      to: "/movies",
      done: state.hasLibrary
    }
  ];

  const completedCount = steps.filter((s) => s.done).length;

  return (
    <section
      aria-label="Getting started with Deluno"
      className={cn(
        "relative overflow-hidden rounded-2xl border border-primary/25 bg-gradient-to-br from-primary/[0.08] via-primary/[0.04] to-transparent p-5",
        "dark:border-primary/30 dark:from-primary/[0.14] dark:via-primary/[0.07]"
      )}
    >
      <div
        aria-hidden
        className="pointer-events-none absolute -right-20 -top-20 h-64 w-64 rounded-full bg-primary/15 blur-[80px]"
      />
      <button
        type="button"
        onClick={() => {
          try {
            window.localStorage.setItem(DISMISS_KEY, "1");
          } catch {
            /* noop */
          }
          setDismissed(true);
        }}
        aria-label="Dismiss onboarding"
        className="absolute right-3 top-3 flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition hover:bg-muted/40 hover:text-foreground"
      >
        <X className="h-3.5 w-3.5" />
      </button>

      <div className="relative flex items-start gap-4">
        <span className="mt-0.5 flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-primary/15 text-primary ring-1 ring-inset ring-primary/25">
          <Sparkles className="h-5 w-5" strokeWidth={2.1} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
            <h2 className="font-display text-lg font-bold tracking-tight text-foreground">
              Finish setting up Deluno
            </h2>
            <span className="tabular text-[12px] text-muted-foreground">
              {completedCount}/{steps.length} complete
            </span>
          </div>
          <p className="mt-0.5 text-[13px] text-muted-foreground">
            Guided setup creates folders, quality profiles, release rules, provider routing, and the first library baseline.
          </p>

          <div className="mt-4 flex flex-wrap items-center gap-2">
            <Link
              to="/setup-guide"
              className="inline-flex h-[var(--control-height-sm)] items-center gap-2 rounded-xl bg-primary px-4 text-[13px] font-semibold text-primary-foreground shadow-glow transition hover:-translate-y-0.5"
            >
              Start guided setup
              <ArrowRight className="h-3.5 w-3.5" />
            </Link>
            <Link
              to="/settings"
              className="inline-flex h-[var(--control-height-sm)] items-center rounded-xl border border-hairline bg-card px-4 text-[13px] font-semibold text-muted-foreground transition hover:text-foreground"
            >
              Advanced settings
            </Link>
          </div>

          <ol className="mt-4 grid gap-2 sm:grid-cols-2 xl:grid-cols-5">
            {steps.map((step, i) => (
              <li key={step.label}>
                <Link
                  to={step.to}
                  className={cn(
                    "group flex h-full items-start gap-2 rounded-xl border border-hairline bg-card/80 p-3 text-left transition",
                    "hover:-translate-y-[1px] hover:border-primary/35 hover:shadow-md",
                    "dark:border-white/[0.06] dark:bg-white/[0.02]",
                    step.done && "opacity-70"
                  )}
                >
                  <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full text-[10px] font-bold tabular">
                    {step.done ? (
                      <CheckCircle2 className="h-5 w-5 text-success" />
                    ) : (
                      <Circle className="h-5 w-5 text-muted-foreground/40" />
                    )}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span
                      className={cn(
                        "block text-[13px] font-semibold text-foreground",
                        step.done && "line-through decoration-muted-foreground/40"
                      )}
                    >
                      {i + 1}. {step.label}
                    </span>
                    <span className="mt-0.5 block text-[11.5px] text-muted-foreground">
                      {step.hint}
                    </span>
                  </span>
                </Link>
              </li>
            ))}
          </ol>
        </div>
      </div>
    </section>
  );
}
