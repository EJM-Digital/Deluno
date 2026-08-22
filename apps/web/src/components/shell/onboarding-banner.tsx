/**
 * First-run onboarding banner.
 *
 * The guided setup prompt is an assisted path, not the authoritative setup
 * state. The dashboard's persistent setup ladder owns readiness; this prompt
 * can be dismissed without changing any ladder step.
 *
 * Drop into the dashboard's header region; renders nothing once the
 * user has either completed all steps or dismissed the banner.
 */

import { Link } from "react-router-dom";
import { ArrowRight, Sparkles, X } from "lucide-react";
import { useState } from "react";
import { cn } from "../../lib/utils";

export function OnboardingBanner({
  isSetupSuppressed,
  onDismiss
}: {
  isSetupSuppressed: boolean;
  onDismiss: () => void;
}) {
  const [dismissed, setDismissed] = useState(false);

  if (isSetupSuppressed || dismissed) return null;

  return (
    <section
      aria-label="Guided setup"
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
          </div>
          <p className="mt-0.5 text-[length:var(--type-body-sm)] text-muted-foreground">
            This assisted path walks through the full baseline. You can dismiss it at any time and use the persistent setup ladder below to complete individual steps in order.
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

        </div>
      </div>
    </section>
  );
}
