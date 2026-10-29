import { toast } from "../components/shell/toaster";
import { plural } from "./plural";

/** What the move/reassign API returns about titles it would not move (#1078). */
export interface MoveResult {
  reassigned?: number;
  filesMoved?: number;
  moveFailures?: string[];
  moveFailureReasons?: string[];
}

/**
 * One plain sentence per refused title. Falls back to the bare title when an
 * older host sent no reason, so a refusal is never reduced to a count.
 */
export function moveRefusalLines(result: MoveResult): string[] {
  const reasons = result.moveFailureReasons ?? [];
  if (reasons.length > 0) return reasons;
  return (result.moveFailures ?? []).map((title) => `${title}: Deluno did not move it, so it stayed where it was.`);
}

/**
 * A refusal stays on screen until it is dismissed: it lists each title with
 * its own reason and is never auto-dismissed before it can be read.
 */
export function toastMoveRefusals(lines: string[]) {
  if (lines.length === 0) return;
  toast.error(
    `${plural(lines.length, "title")} not moved`,
    {
      duration: Infinity,
      description: (
        <ul className="mt-1 list-disc space-y-1 pl-4" aria-label="Titles Deluno did not move and why">
          {lines.map((line) => <li key={line}>{line}</li>)}
        </ul>
      )
    }
  );
}
