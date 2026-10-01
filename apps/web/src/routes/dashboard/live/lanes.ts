/**
 * The look of each station of the Live journey (#1098): its name, icon and accent
 * colour. One place, so the stations, the cards in them and the activity all say the
 * same thing about each stage.
 */
import { Download, FolderInput, HardDriveDownload, Inbox, Library, ShieldCheck, type LucideIcon } from "lucide-react";
import type { Tone } from "../../../lib/status-tones";
import type { Stage } from "./journey";

export interface LaneStyle {
  label: string;
  icon: LucideIcon;
  /** The accent: the station's ring, the stripe on each card and the glow of a card arriving. */
  accent: string;
  text: string;
  to: string;
}

export const LANES: Record<Stage, LaneStyle> = {
  requested: { label: "Requested", icon: Inbox, accent: "--stage-search", text: "text-[hsl(var(--stage-search))]", to: "/indexers/media-servers" },
  grabbed: { label: "Grabbed", icon: HardDriveDownload, accent: "--stage-grab", text: "text-[hsl(var(--stage-grab))]", to: "/queue" },
  downloading: { label: "Downloading", icon: Download, accent: "--info", text: "text-info", to: "/queue" },
  processor: { label: "Processing", icon: ShieldCheck, accent: "--stage-process", text: "text-[hsl(var(--stage-process))]", to: "/queue" },
  importing: { label: "Importing", icon: FolderInput, accent: "--stage-import", text: "text-[hsl(var(--stage-import))]", to: "/queue" },
  imported: { label: "In the library", icon: Library, accent: "--success", text: "text-success", to: "/activity" }
};

export const TONE_TEXT: Record<Tone, string> = { ok: "text-success", warn: "text-warning", bad: "text-destructive", info: "text-foreground", idle: "text-muted-foreground" };
