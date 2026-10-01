/**
 * Connections (#1098): everything else Deluno is connected to, one tile each with
 * its role and what it did today ("Processor · working on 1", "Media server · 5
 * sent today", "Requests · 3 today", "Subtitles · 12 fetched today", "Lists ·
 * watching 2"). A tile lights up when Deluno uses it, and a processor glows while
 * it has a title.
 */
import type { LiveConnection, LiveConnectionKind } from "../../../lib/api/types/operations";
import type { Tone } from "../../../lib/status-tones";
import { connectionKey, connectionWords, connectionsSummary } from "./connections";
import { Tile, TileGroup, type TileState } from "./tile";

const MANAGE: Record<LiveConnectionKind, string> = {
  processor: "/indexers/companion-tools",
  mediaServer: "/indexers/media-servers",
  requests: "/indexers/media-servers",
  subtitles: "/indexers/subtitle-providers",
  lists: "/settings/lists"
};

export function ConnectionTiles({ connections, lit }: { connections: readonly LiveConnection[]; lit: ReadonlySet<string> }) {
  return (
    <TileGroup title="Connections" count={connectionsSummary(connections)} to="/indexers/media-servers" empty="Nothing else is connected yet: a processor, a media server, Seerr, subtitles or a list.">
      {connections.map((connection) => {
        const tone: Tone = !connection.enabled ? "idle" : !connection.healthy ? "bad" : "ok";
        const busy = connection.kind === "processor" && connection.busy > 0;
        const state: TileState | null = !connection.enabled ? "off" : lit.has(connectionKey(connection)) ? "lit" : busy ? "busy" : !connection.healthy ? "failed" : null;
        const words = connectionWords(connection);
        return <Tile key={connectionKey(connection)} to={MANAGE[connection.kind]} name={connection.name} tone={tone} state={state} pulse={busy} meta={words} label={`${connection.name}: ${words}`} />;
      })}
    </TileGroup>
  );
}
