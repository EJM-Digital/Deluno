import { cn } from "../../lib/utils";
import { KNOWN_INDEXER_FLAGS } from "./presets";
import { indexerFlagSelected, toggleIndexerFlag } from "./format";

/**
 * Common flags as toggle chips, supplementing (not replacing) the free-text
 * "preferred indexer flags" field so a site's own unlisted flag can still be
 * typed (#745).
 */
export function IndexerFlagPicker({ value, onChange }: { value: string; onChange: (next: string) => void }) {
  return (
    <div className="flex flex-wrap gap-1.5" role="group" aria-label="Known indexer flags">
      {KNOWN_INDEXER_FLAGS.map((flag) => {
        const selected = indexerFlagSelected(value, flag);
        return (
          <button
            key={flag}
            type="button"
            aria-pressed={selected}
            onClick={() => onChange(toggleIndexerFlag(value, flag))}
            className={cn(
              "inline-flex h-6 shrink-0 items-center whitespace-nowrap rounded-full border px-2.5 text-[length:var(--type-caption)] font-semibold leading-none transition-colors",
              selected ? "border-primary/60 bg-primary/[0.12] text-primary" : "border-hairline bg-surface-2 text-muted-foreground hover:bg-surface-3"
            )}
          >
            {flag}
          </button>
        );
      })}
    </div>
  );
}
