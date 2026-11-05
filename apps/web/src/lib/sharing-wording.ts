import { pluralNoun } from "./plural";

/** One run of the wording; `strong` runs are the figures a card draws bold. */
export interface SharingPart {
  text: string;
  strong: boolean;
}

const count = (n: number) => n.toLocaleString("en-AU");

/**
 * #1516: what a torrent download is fed by, in plain words: "2 seeders · 5 peers", or "3 of 41 seeders · 5 peers" when the client also
 * says how many seeders the whole swarm has. A client that does not report seeders (and every Usenet download) keeps the short form,
 * "5 peers" for a torrent and "8 connections" for Usenet. Peers is every connected peer, seeders included.
 */
export function sharingParts(peers: number, seeders: number | null | undefined, swarmSeeders: number | null | undefined, usenet: boolean): SharingPart[] {
  if (usenet || seeders == null) {
    return [
      { text: count(peers), strong: true },
      { text: ` ${pluralNoun(peers, usenet ? "connection" : "peer")}`, strong: false }
    ];
  }
  const swarm = swarmSeeders != null && swarmSeeders >= seeders && swarmSeeders > 0 ? swarmSeeders : null;
  return [
    { text: swarm === null ? count(seeders) : `${count(seeders)} of ${count(swarm)}`, strong: true },
    { text: ` ${pluralNoun(swarm ?? seeders, "seeder")} · `, strong: false },
    { text: count(peers), strong: true },
    { text: ` ${pluralNoun(peers, "peer")}`, strong: false }
  ];
}

export function sharingText(peers: number, seeders: number | null | undefined, swarmSeeders: number | null | undefined, usenet: boolean): string {
  return sharingParts(peers, seeders, swarmSeeders, usenet).map((part) => part.text).join("");
}
