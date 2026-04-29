import type { ComponentType } from "react";
import {
  Activity,
  BookOpen,
  Download,
  Film,
  FolderTree,
  KeyRound,
  RadioTower,
  Route,
  Settings,
  SlidersHorizontal,
  Sparkles,
  Tags,
  Tv
} from "lucide-react";

export type OperationMode = "quick" | "advanced";
export type OperationArea = "library" | "queue" | "providers" | "policy" | "system";

export interface OperationPath {
  id: string;
  title: string;
  description: string;
  to: string;
  area: OperationArea;
  mode: OperationMode;
  icon: ComponentType<{ className?: string }>;
  keywords: string[];
}

export const operationPaths: OperationPath[] = [
  {
    id: "movies",
    title: "Manage movies",
    description: "Add titles, review wanted state, search manually, and open the full movie workspace.",
    to: "/movies",
    area: "library",
    mode: "quick",
    icon: Film,
    keywords: ["movie", "library", "add movie", "wanted"]
  },
  {
    id: "tv",
    title: "Manage TV shows",
    description: "Add series, review episodes, search manually, and open the full show workspace.",
    to: "/tv",
    area: "library",
    mode: "quick",
    icon: Tv,
    keywords: ["tv", "shows", "series", "episodes"]
  },
  {
    id: "queue",
    title: "Downloads and imports",
    description: "One queue for client telemetry, import previews, recovery cases, and manual imports.",
    to: "/queue",
    area: "queue",
    mode: "quick",
    icon: Download,
    keywords: ["queue", "download", "import", "recovery", "manual import"]
  },
  {
    id: "sources",
    title: "Sources and clients",
    description: "Configure indexers, download clients, health tests, and library provider routing.",
    to: "/indexers",
    area: "providers",
    mode: "quick",
    icon: RadioTower,
    keywords: ["indexer", "download client", "source", "provider", "routing"]
  },
  {
    id: "setup",
    title: "Guided setup",
    description: "Create the safe baseline first, then refine advanced policy only when needed.",
    to: "/setup-guide",
    area: "policy",
    mode: "quick",
    icon: Sparkles,
    keywords: ["setup", "wizard", "beginner", "baseline"]
  },
  {
    id: "folders",
    title: "Folders and naming",
    description: "Set root paths, folder formats, rename rules, hardlinks, and import behaviour.",
    to: "/settings/media-management",
    area: "policy",
    mode: "advanced",
    icon: FolderTree,
    keywords: ["folders", "paths", "naming", "hardlinks", "rename"]
  },
  {
    id: "destination-rules",
    title: "Destination rules",
    description: "Route content by media type, genre, tags, language, quality, and workflow.",
    to: "/settings/destination-rules",
    area: "policy",
    mode: "advanced",
    icon: Route,
    keywords: ["destination", "rules", "route", "genre", "root folder"]
  },
  {
    id: "profiles",
    title: "Quality profiles",
    description: "Define cutoff quality, upgrade behaviour, and safe defaults per library intent.",
    to: "/settings/profiles",
    area: "policy",
    mode: "advanced",
    icon: SlidersHorizontal,
    keywords: ["quality", "profiles", "cutoff", "upgrades"]
  },
  {
    id: "formats",
    title: "Custom formats",
    description: "Score releases using source, codec, HDR, language, release group, bitrate, and tags.",
    to: "/settings/custom-formats",
    area: "policy",
    mode: "advanced",
    icon: Tags,
    keywords: ["custom formats", "scoring", "release group", "bitrate", "tags"]
  },
  {
    id: "activity",
    title: "Activity trail",
    description: "Review searches, imports, jobs, suppressions, retries, skips, and failures.",
    to: "/activity",
    area: "system",
    mode: "quick",
    icon: Activity,
    keywords: ["activity", "history", "audit", "events"]
  },
  {
    id: "api",
    title: "API access",
    description: "Generate and revoke keys for trusted integrations, scripts, and dashboards.",
    to: "/system/api",
    area: "system",
    mode: "advanced",
    icon: KeyRound,
    keywords: ["api", "keys", "integration", "token"]
  },
  {
    id: "guide",
    title: "Workflow guide",
    description: "Plain-English help for routing, scoring, imports, integrations, and recovery.",
    to: "/system/docs",
    area: "system",
    mode: "quick",
    icon: BookOpen,
    keywords: ["docs", "guide", "help", "workflow"]
  },
  {
    id: "settings",
    title: "Advanced settings",
    description: "Tune platform defaults, interface density, metadata, tags, lists, and policy internals.",
    to: "/settings",
    area: "system",
    mode: "advanced",
    icon: Settings,
    keywords: ["settings", "advanced", "interface", "metadata", "lists"]
  }
];

export const quickOperationPaths = operationPaths.filter((path) => path.mode === "quick");
export const advancedOperationPaths = operationPaths.filter((path) => path.mode === "advanced");

export function operationPathById(id: string) {
  return operationPaths.find((path) => path.id === id);
}
