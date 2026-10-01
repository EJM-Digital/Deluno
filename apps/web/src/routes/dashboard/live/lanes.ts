/**
 * The look of each lane of the Live board (#1096): its name, icon, accent colour
 * and where it leads. One place, so the lanes, the cards in them and the quiet
 * column all say the same thing about each stage.
 */
import { AlertOctagon, Download, FolderInput, HardDriveDownload, Inbox, Library, Search, ShieldCheck, type LucideIcon } from "lucide-react";
import type { Tone } from "../../../lib/status-tones";
import type { Stage } from "./journey";

export interface LaneStyle {
  label: string;
  icon: LucideIcon;
  /** The accent: a bar across the top of the lane, a stripe on each card and the glow of a card arriving. */
  accent: string;
  bar: string;
  text: string;
  to: string;
}

export const LANES: Record<Stage, LaneStyle> = {
  requested: { label: "Requested", icon: Inbox, accent: "--primary", bar: "bg-primary", text: "text-primary", to: "/indexers/media-servers" },
  searching: { label: "Searching", icon: Search, accent: "--stage-search", bar: "bg-[hsl(var(--stage-search))]", text: "text-[hsl(var(--stage-search))]", to: "/search-cycles" },
  grabbed: { label: "Grabbed", icon: HardDriveDownload, accent: "--stage-grab", bar: "bg-[hsl(var(--stage-grab))]", text: "text-[hsl(var(--stage-grab))]", to: "/queue" },
  downloading: { label: "Downloading", icon: Download, accent: "--info", bar: "bg-info", text: "text-info", to: "/queue" },
  needs: { label: "Needs you", icon: AlertOctagon, accent: "--destructive", bar: "bg-destructive", text: "text-destructive", to: "/queue" },
  processor: { label: "With the processor", icon: ShieldCheck, accent: "--stage-process", bar: "bg-[hsl(var(--stage-process))]", text: "text-[hsl(var(--stage-process))]", to: "/queue" },
  importing: { label: "Importing", icon: FolderInput, accent: "--stage-import", bar: "bg-[hsl(var(--stage-import))]", text: "text-[hsl(var(--stage-import))]", to: "/queue" },
  imported: { label: "In the library", icon: Library, accent: "--success", bar: "bg-success", text: "text-success", to: "/activity" }
};

export const TONE_TEXT: Record<Tone, string> = { ok: "text-success", warn: "text-warning", bad: "text-destructive", info: "text-foreground", idle: "text-muted-foreground" };
