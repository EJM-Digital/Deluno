import type { ProcessorOutcomeRecord } from "./api";

/**
 * How a hand-off's outcome report stands, in the words the Transfers drawer
 * uses (#580). Mirrors `ProcessorOutcomeDeliveries` in
 * `Deluno.Platform.Contracts`.
 */
export const PROCESSOR_OUTCOME_DELIVERY = {
  pending: "pending",
  delivered: "delivered",
  refused: "refused",
  settled: "settled",
  neverReceived: "never-received",
  replaced: "replaced",
  givenUp: "given-up"
} as const;

export interface ProcessorOutcomeSummary {
  /** What Deluno told the processor. */
  said: string;
  /** Whether it heard, and what happens next. */
  heard: string;
  /** The processor's own words, or why it could not be asked. */
  detail: string | null;
  /** Anything but an acknowledged report can be said again. */
  canSendAgain: boolean;
}

export function processorOutcomeSummary(
  outcome: ProcessorOutcomeRecord,
  formatWhen: (utc: string) => string
): ProcessorOutcomeSummary {
  const said = outcome.outcome === "imported" ? "It has the file" : "It will not import the file";
  const tries = outcome.attempts === 1 ? "1 attempt" : `${outcome.attempts} attempts`;
  switch (outcome.delivery) {
    case PROCESSOR_OUTCOME_DELIVERY.delivered:
      return {
        said,
        heard: outcome.released ? "Yes, and it released its copy" : "Yes, and it kept its copy",
        detail: outcome.released ? null : outcome.processorSaid,
        canSendAgain: false
      };
    case PROCESSOR_OUTCOME_DELIVERY.pending:
      return {
        said,
        heard: outcome.nextAttemptUtc
          ? `Not yet · ${tries} · next ${formatWhen(outcome.nextAttemptUtc)}`
          : `Not yet · ${tries}`,
        detail: outcome.processorSaid,
        canSendAgain: true
      };
    case PROCESSOR_OUTCOME_DELIVERY.settled:
      return { said, heard: "It kept its earlier answer, so Deluno stopped telling it", detail: outcome.processorSaid, canSendAgain: false };
    case PROCESSOR_OUTCOME_DELIVERY.neverReceived:
      return { said, heard: "It never received this hand-off, so there was nothing to tell it", detail: null, canSendAgain: false };
    case PROCESSOR_OUTCOME_DELIVERY.replaced:
      return { said, heard: "A newer send of this release took over, so this one was not told", detail: null, canSendAgain: false };
    case PROCESSOR_OUTCOME_DELIVERY.refused:
      return { said, heard: "Refused", detail: outcome.processorSaid, canSendAgain: true };
    default:
      return { said, heard: "Deluno stopped trying", detail: outcome.processorSaid, canSendAgain: true };
  }
}
