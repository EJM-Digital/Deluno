/**
 * InfoHint — the one (i) icon that shows and hides a longer description (#1430).
 *
 * Deluno explains itself in a short line, and only a setting or section that truly needs more carries an (i) beside
 * its name, as Radarr and Sonarr do. The icon is a button that toggles the description; the description is a note
 * placed right where the icon sits, so it is read in context and nothing else on the page carries long text.
 *
 * Use it through `Field`'s `info` prop and `ListCard`'s `info` prop rather than by hand, so every (i) looks and
 * behaves the same.
 */
import { useId, useState, type ReactNode } from "react";
import { Info } from "lucide-react";
import { cn } from "../../lib/utils";

/** The button. `about` names what it explains, so each icon on a page has its own accessible name. */
export function InfoHintButton({ about, open, onToggle, describes, className }: { about: string; open: boolean; onToggle: () => void; describes: string; className?: string }) {
  return (
    <button
      type="button"
      aria-label={`More about ${about}`}
      aria-expanded={open}
      aria-controls={describes}
      onClick={onToggle}
      className={cn(
        "inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-full text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring",
        open && "text-primary",
        className
      )}
    >
      <Info aria-hidden className="h-4 w-4" />
    </button>
  );
}

/** The longer description. Not rendered at all while hidden, so it adds nothing to the page until asked for. */
export function InfoHintBody({ id, children, className }: { id: string; children: ReactNode; className?: string }) {
  return (
    <p id={id} role="note" className={cn("max-w-prose text-(length:--type-caption) leading-relaxed text-muted-foreground", className)}>
      {children}
    </p>
  );
}

/** State for one icon and its description: `{ open, toggle, bodyId }`. */
export function useInfoHint() {
  const [open, setOpen] = useState(false);
  const bodyId = useId();
  return { open, toggle: () => setOpen((value) => !value), bodyId };
}
