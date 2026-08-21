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
import { useState } from "react";
import { cn } from "../../lib/utils";

export interface OnboardingState {
  hasIndexer: boolean;
  hasDownloadClient: boolean;
  hasLibrary: boolean;
}

export function OnboardingBanner({
  state,
  isSetupSuppressed,
  onDismiss
}: {
  state: OnboardingState;
  isSetupSuppressed: boolean;
  onDismiss: () => void;
}) {
  const [dismissed, setDismissed] = useState(false);

  const allDone = state.hasIndexer && state.hasDownloadClient && state.hasLibrary;
  if (allDone || isSetupSuppressed || dismissed) return null;

  const steps: { label: string; to: string; done: boolean; hint: string }[] = [
    {
      label: "Choose media folders",
      hint: "Where your media and completed downloads will live.",
      to: "/setup-guide",
      done: state.hasLibrary
    },
    {
      label: "Choose a media plan",
      hint: "Quality, storage, and release preferences in plain language.",
      to: "/setup-guide",
      done: state.hasLibrary
    },
    {
      label: "Connect a search source",
      hint: "Where Deluno looks for releases when your library needs one.",
      to: "/indexers",
      done: state.hasIndexer
    },
    {
      label: "Choose downloads",
      hint: "Where approved releases go before Deluno imports them.",
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
      {/* The content block below is `relative`, so it painted over this button and
          swallowed the clicks — the X read as misaligned when it was simply covered. */}
      <button
        type="button"
        onClick={() => {
          setDismissed(true);
          onDismiss();
        }}
        aria-label="Dismiss onboarding"
        className="absolute right-2 top-2 z-10 flex h-9 w-9 items-center justify-center rounded-lg text-muted-foreground transition hover:bg-muted/40 hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <X className="h-4 w-4" />
      </button>

      <div className="relative flex items-start gap-[var(--grid-gap)]">
        <span className="mt-0.5 flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-primary/15 text-primary ring-1 ring-inset ring-primary/25">
          <Sparkles className="h-5 w-5" strokeWidth={2.1} />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
            <h2 className="font-display text-lg font-bold tracking-tight text-foreground">
              Build your media library
            </h2>
            <span className="tabular text-[length:var(--type-caption)] text-muted-foreground">
              {completedCount}/{steps.length} complete
            </span>
          </div>
          <p className="mt-0.5 text-[length:var(--type-body-sm)] text-muted-foreground">
            Tell Deluno what kind of library you want. It will create the folders, media plan, release rules, routing, and first library baseline.
          </p>

          <div className="mt-4 flex flex-wrap items-center gap-2">
            <Link
              to="/setup-guide"
              className="inline-flex h-[var(--control-height-sm)] items-center gap-2 rounded-xl bg-primary px-4 text-[length:var(--type-body-sm)] font-semibold text-primary-foreground shadow-glow transition hover:-translate-y-0.5"
            >
              Build my setup
              <ArrowRight className="h-3.5 w-3.5" />
            </Link>
            <Link
              to="/settings"
              className="inline-flex h-[var(--control-height-sm)] items-center rounded-xl border border-hairline bg-card px-4 text-[length:var(--type-body-sm)] font-semibold text-muted-foreground transition hover:text-foreground"
            >
              Open library setup
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
                  <span className="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full text-[length:var(--type-micro)] font-bold tabular">
                    {step.done ? (
                      <CheckCircle2 className="h-5 w-5 text-success" />
                    ) : (
                      <Circle className="h-5 w-5 text-muted-foreground/40" />
                    )}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span
                      className={cn(
                        "block text-[length:var(--type-body-sm)] font-semibold text-foreground",
                        step.done && "line-through decoration-muted-foreground/40"
                      )}
                    >
                      {i + 1}. {step.label}
                    </span>
                    <span className="mt-0.5 block text-[length:var(--type-caption)] text-muted-foreground">
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
