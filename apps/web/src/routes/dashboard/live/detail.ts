/**
 * The detail block under a journey card's status line (#1098), as the approved mockup's `paintToken` writes it: what
 * matters at this stage, up to three lines, never the thing the status line already says. Every line comes from a real
 * reading (the sent download, the client's own queue item, the processor's own report) and a line with no real value is
 * left out rather than guessed. How many of them the card shows is decided by its height (see `fitDetails`).
 */
import { ago, formatEta } from "../downloads-kit";
import { formatBytes } from "../../../lib/utils";
import type { JourneyCard } from "./journey";
import { startedLabel } from "./provenance";
import { doneAbout } from "./times";

/** A piece of a line: plain words, or a bold one. */
export type Part = string | { bold: string };

export interface DetailLine {
  parts: Part[];
  /** The quiet text at the right of the line. */
  right?: string;
  /** The release's own name: small and monospaced. */
  mono?: boolean;
}

const bold = (text: string): Part => ({ bold: text });

/** "1.2 of 3.2 GB": the two sizes with their unit said once when it is the same. */
export function sizePair(done: number, total: number): string {
  const first = formatBytes(done).split(" ");
  const second = formatBytes(total).split(" ");
  return first[1] !== undefined && first[1] === second[1] ? `${first[0]} of ${second[0]} ${second[1]}` : `${formatBytes(done)} of ${formatBytes(total)}`;
}

/** How long ago something happened, as a duration: "4 min". Nothing when the time is unknown. */
export function spent(from: string | null | undefined, now: number): string | undefined {
  const start = Date.parse(from ?? "");
  if (!Number.isFinite(start) || now <= start) return undefined;
  const text = formatEta(Math.max(60, Math.round((now - start) / 1000)));
  return text === "–" ? undefined : text;
}

export function detailLines(card: JourneyCard, indexers: number, now: number): DetailLine[] {
  const lines: DetailLine[] = [];
  const size = card.sizeBytes && card.sizeBytes > 0 ? card.sizeBytes : undefined;
  const release = card.release ? ({ parts: [card.release], mono: true } satisfies DetailLine) : undefined;
  switch (card.stage) {
    case "requested": {
      const asked = card.routeNote?.startsWith("Asked by ") ? ["Asked by ", bold(card.routeNote.slice("Asked by ".length))] : card.routeNote ? [card.routeNote] : [];
      if (asked.length > 0) lines.push({ parts: asked, right: card.at ? ago(card.at, now) || undefined : undefined });
      // The status line says how many indexers a plain search is looking on; a request says it here.
      if (card.startedBy === "seerr" && indexers > 0) lines.push({ parts: [bold(indexers.toLocaleString("en-AU")), ` ${indexers === 1 ? "indexer" : "indexers"} to ask`] });
      break;
    }
    case "grabbed": {
      if (release) lines.push(release);
      if (size) lines.push({ parts: [bold(formatBytes(size))], right: card.client ? `to ${card.client}` : undefined });
      break;
    }
    case "downloading": {
      const have = card.downloadedBytes ?? (size && card.progress !== undefined ? Math.round((size * card.progress) / 100) : undefined);
      const doneBy = card.stuck ? undefined : doneAbout(card.finishAtMs) ?? undefined;
      if (size && have !== undefined) lines.push({ parts: [bold(sizePair(have, size))], right: doneBy });
      else if (doneBy) lines.push({ parts: [], right: doneBy });
      if (card.stuck) break;
      if (card.peers) lines.push({ parts: [bold(card.peers.toLocaleString("en-AU")), ` ${card.protocol === "usenet" ? (card.peers === 1 ? "connection" : "connections") : card.peers === 1 ? "peer" : "peers"}`] });
      if (release) lines.push(release);
      break;
    }
    case "processor": {
      if (card.processorStep) lines.push({ parts: [bold(card.processorStep)] });
      lines.push({ parts: size ? [bold(formatBytes(size)), " handed over"] : ["Handed over"], right: spent(card.handedOverUtc, now) });
      if (card.processorQueuePosition && card.processorQueuePosition > 0) lines.push({ parts: [`Number ${card.processorQueuePosition.toLocaleString("en-AU")} in its line`] });
      if (release) lines.push(release);
      break;
    }
    case "importing": {
      if (card.destination) lines.push({ parts: ["To ", bold(card.destination)] });
      const facts = [size ? formatBytes(size) : null, card.quality ?? null].filter(Boolean) as string[];
      if (facts.length > 0) lines.push({ parts: [bold(facts[0]), ...(facts.length > 1 ? [` · ${facts[1]}`] : [])] });
      if (release) lines.push(release);
      break;
    }
    default:
      break;
  }
  // Why Deluno is fetching it, when what it recorded says (a request already says so in its own line).
  if (card.startedBy && !(card.stage === "requested" && card.startedBy === "seerr")) lines.push({ parts: [bold(startedLabel(card.startedBy, card.entity, card.release))] });
  return lines.slice(0, 4);
}

/**
 * How many detail lines a card has room for, the mockup's rule: the lines start where the block starts, the card keeps
 * 8px under them, and a line is 15px.
 */
export function detailRoom(cardBottom: number, blockTop: number): number {
  return Math.max(0, Math.floor((cardBottom - 8 - blockTop) / 15));
}
