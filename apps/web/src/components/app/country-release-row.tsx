/**
 * One country's release dates and certification (#862), shared by the movie
 * and show detail pages rather than each growing its own copy of "format a
 * `CountryReleaseFacts` row" — the same reason `CreditsRow` is one component
 * instead of two.
 */
import { formatShortDate, type DisplayPreferences } from "../../lib/display-preferences";
import type { CountryReleaseFacts } from "../../lib/ui-adapters";
import { cn } from "../../lib/utils";

interface CountryReleaseRowProps {
  facts: CountryReleaseFacts;
  preferences: DisplayPreferences;
  /** The certification-country setting's own pick, shown ahead of the rest. */
  highlighted?: boolean;
}

export function CountryReleaseRow({ facts, preferences, highlighted }: CountryReleaseRowProps) {
  const dates = [
    facts.inCinemas ? `Cinemas ${formatShortDate(facts.inCinemas, { ...preferences, showRelativeDates: false })}` : null,
    facts.digital ? `Digital ${formatShortDate(facts.digital, { ...preferences, showRelativeDates: false })}` : null,
    facts.physical ? `Physical ${formatShortDate(facts.physical, { ...preferences, showRelativeDates: false })}` : null
  ].filter(Boolean);

  return (
    <div className={cn("flex flex-wrap items-center gap-x-3 gap-y-1 rounded-lg px-2 py-1.5", highlighted && "bg-primary/10")}>
      <span className="min-w-12 font-mono text-xs font-semibold uppercase text-foreground">{facts.country}</span>
      {facts.certification ? (
        <span className="rounded-sm border border-hairline px-1.5 py-px text-xs font-bold uppercase tracking-wide">{facts.certification}</span>
      ) : null}
      {dates.length ? (
        <span className="text-xs text-muted-foreground">{dates.join(" · ")}</span>
      ) : (
        <span className="text-xs text-muted-foreground">No release date on file</span>
      )}
    </div>
  );
}
