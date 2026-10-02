/**
 * The look of each station of the Live journey (#1098): its name, icon and accent
 * colour. One place, so the stations, the cards in them and the activity all say the
 * same thing about each stage.
 */
import { Download, FolderInput, HardDriveDownload, Inbox, Library, ShieldCheck, type LucideIcon } from "lucide-react";
import type { Tone } from "../../../lib/status-tones";
import type { Stage } from "./journey";
import { activityStatusLink } from "../../../lib/activity-history";

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
  grabbed: { label: "Grabbed", icon: HardDriveDownload, accent: "--journey-grab", text: "text-[hsl(var(--journey-grab))]", to: activityStatusLink("in-progress") },
  downloading: { label: "Downloading", icon: Download, accent: "--info", text: "text-info", to: activityStatusLink("in-progress") },
  processor: { label: "Processing", icon: ShieldCheck, accent: "--journey-process", text: "text-[hsl(var(--journey-process))]", to: activityStatusLink("in-progress") },
  importing: { label: "Importing", icon: FolderInput, accent: "--journey-import", text: "text-[hsl(var(--journey-import))]", to: activityStatusLink("in-progress") },
  imported: { label: "In the library", icon: Library, accent: "--success", text: "text-success", to: activityStatusLink("done") }
};

export const TONE_TEXT: Record<Tone, string> = { ok: "text-success", warn: "text-warning", bad: "text-destructive", info: "text-foreground", idle: "text-muted-foreground" };
