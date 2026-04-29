import type { ComponentType } from "react";
import {
  Activity,
  Calendar,
  Clapperboard,
  Command,
  Cpu,
  Download,
  Film,
  FolderTree,
  Keyboard,
  LayoutDashboard,
  ListChecks,
  Moon,
  Palette,
  RadioTower,
  RefreshCw,
  Search,
  Settings,
  SlidersHorizontal,
  Sparkles,
  Stars,
  Sun,
  Tag,
  Tv,
  Wand2
} from "lucide-react";

export type CommandGroup = "navigation" | "actions" | "recents" | "preferences";

export interface CommandItem {
  id: string;
  label: string;
  keywords?: string[];
  group: CommandGroup;
  icon?: ComponentType<{ className?: string }>;
  shortcut?: string[];
  to?: string;
  perform?: () => void;
}

export interface ShortcutItem {
  keys: string[];
  label: string;
  group: string;
}

export const navigationCommands: CommandItem[] = [
  {
    id: "nav.overview",
    label: "Overview",
    keywords: ["dashboard", "home", "ops"],
    group: "navigation",
    icon: LayoutDashboard,
    to: "/",
    shortcut: ["g", "o"]
  },
  {
    id: "nav.movies",
    label: "Movies",
    keywords: ["library", "films"],
    group: "navigation",
    icon: Film,
    to: "/movies",
    shortcut: ["g", "m"]
  },
  {
    id: "nav.tv",
    label: "TV",
    keywords: ["shows", "series"],
    group: "navigation",
    icon: Tv,
    to: "/tv",
    shortcut: ["g", "t"]
  },
  {
    id: "nav.indexers",
    label: "Sources and clients",
    keywords: ["providers", "prowlarr", "sources", "indexers", "download clients"],
    group: "navigation",
    icon: RadioTower,
    to: "/indexers",
    shortcut: ["g", "i"]
  },
  {
    id: "nav.queue",
    label: "Queue",
    keywords: ["downloads", "imports", "clients", "recovery"],
    group: "navigation",
    icon: Download,
    to: "/queue",
    shortcut: ["g", "q"]
  },
  {
    id: "nav.activity",
    label: "Activity",
    keywords: ["queue", "history", "jobs", "downloads"],
    group: "navigation",
    icon: Activity,
    to: "/activity",
    shortcut: ["g", "a"]
  },
  {
    id: "nav.calendar",
    label: "Calendar",
    keywords: ["schedule", "airing"],
    group: "navigation",
    icon: Calendar,
    to: "/calendar",
    shortcut: ["g", "c"]
  },
  {
    id: "nav.settings",
    label: "Settings",
    keywords: ["preferences", "config"],
    group: "navigation",
    icon: Settings,
    to: "/settings",
    shortcut: ["g", "s"]
  },
  {
    id: "nav.system",
    label: "System",
    keywords: ["logs", "tasks", "diagnostics"],
    group: "navigation",
    icon: Cpu,
    to: "/system",
    shortcut: ["g", "y"]
  }
];

export const settingsCommands: CommandItem[] = [
  {
    id: "settings.general",
    label: "Settings · System · General",
    keywords: ["system", "startup", "url", "branding", "app", "instance"],
    group: "navigation",
    icon: Settings,
    to: "/settings/general"
  },
  {
    id: "settings.ui",
    label: "Settings · System · Interface",
    keywords: ["system", "appearance", "theme", "density", "display", "ui", "interface"],
    group: "navigation",
    icon: Palette,
    to: "/settings/ui"
  },
  {
    id: "settings.media-management",
    label: "Settings · Library · Media Management",
    keywords: ["library", "folders", "paths", "naming", "import", "organise", "root"],
    group: "navigation",
    icon: FolderTree,
    to: "/settings/media-management"
  },
  {
    id: "settings.destination-rules",
    label: "Settings · Library · Destination Rules",
    keywords: ["library", "routing", "rules", "root folders", "genre", "tag", "language"],
    group: "navigation",
    icon: FolderTree,
    to: "/settings/destination-rules"
  },
  {
    id: "settings.metadata",
    label: "Settings · Library · Metadata",
    keywords: ["library", "tmdb", "tvdb", "fanart", "metadata", "nfo"],
    group: "navigation",
    icon: Sparkles,
    to: "/settings/metadata"
  },
  {
    id: "settings.tags",
    label: "Settings · Library · Tags",
    keywords: ["library", "tags", "labels", "groups", "routing"],
    group: "navigation",
    icon: Tag,
    to: "/settings/tags"
  },
  {
    id: "settings.policy-sets",
    label: "Settings · Quality · Policy Sets",
    keywords: ["quality", "policy", "routing", "destination", "multi version"],
    group: "navigation",
    icon: Stars,
    to: "/settings/policy-sets"
  },
  {
    id: "settings.profiles",
    label: "Settings · Quality · Profiles",
    keywords: ["quality", "profiles", "policy", "upgrades"],
    group: "navigation",
    icon: Stars,
    to: "/settings/profiles"
  },
  {
    id: "settings.quality",
    label: "Settings · Quality · Size Rules",
    keywords: ["quality", "resolution", "bitrate", "size limits", "sizes"],
    group: "navigation",
    icon: SlidersHorizontal,
    to: "/settings/quality"
  },
  {
    id: "settings.custom-formats",
    label: "Settings · Quality · Custom Formats",
    keywords: ["quality", "scoring", "release", "format", "rules"],
    group: "navigation",
    icon: Wand2,
    to: "/settings/custom-formats"
  },
  {
    id: "settings.lists",
    label: "Settings · Automation · Intake Sources",
    keywords: ["automation", "imdb", "trakt", "intake", "source", "auto import", "lists"],
    group: "navigation",
    icon: ListChecks,
    to: "/settings/lists"
  }
];

export interface BuildActionCommandsOptions {
  onAddMovie?: () => void;
  onAddSeries?: () => void;
  onAddIndexer?: () => void;
  onRefresh?: () => void;
  onToggleTheme?: () => void;
  theme?: string;
}

export function buildActionCommands({
  onAddMovie,
  onAddSeries,
  onAddIndexer,
  onRefresh,
  onToggleTheme,
  theme
}: BuildActionCommandsOptions): CommandItem[] {
  const items: CommandItem[] = [];
  if (onAddMovie) {
    items.push({
      id: "action.add-movie",
      label: "Add movie",
      group: "actions",
      icon: Clapperboard,
      perform: onAddMovie,
      keywords: ["new", "import"]
    });
  }
  if (onAddSeries) {
    items.push({
      id: "action.add-series",
      label: "Add TV show",
      group: "actions",
      icon: Tv,
      perform: onAddSeries,
      keywords: ["new", "import", "show"]
    });
  }
  if (onAddIndexer) {
    items.push({
      id: "action.add-indexer",
      label: "Add indexer",
      group: "actions",
      icon: RadioTower,
      perform: onAddIndexer,
      keywords: ["provider", "new"]
    });
  }
  if (onRefresh) {
    items.push({
      id: "action.refresh",
      label: "Refresh data",
      group: "actions",
      icon: RefreshCw,
      perform: onRefresh,
      keywords: ["reload", "revalidate"]
    });
  }
  if (onToggleTheme) {
    items.push({
      id: "action.toggle-theme",
      label: theme === "dark" ? "Switch to light mode" : "Switch to dark mode",
      group: "preferences",
      icon: theme === "dark" ? Sun : Moon,
      perform: onToggleTheme,
      keywords: ["dark", "light", "theme"]
    });
  }
  return items;
}

export const globalShortcuts: ShortcutItem[] = [
  { keys: ["Cmd", "K"], label: "Open command palette", group: "Global" },
  { keys: ["/"], label: "Focus search", group: "Global" },
  { keys: ["?"], label: "Show keyboard shortcuts", group: "Global" },
  { keys: ["Esc"], label: "Close overlay or clear selection", group: "Global" },
  { keys: ["g", "o"], label: "Go to Overview", group: "Navigation" },
  { keys: ["g", "m"], label: "Go to Movies", group: "Navigation" },
  { keys: ["g", "t"], label: "Go to TV", group: "Navigation" },
  { keys: ["g", "q"], label: "Go to Queue", group: "Navigation" },
  { keys: ["g", "i"], label: "Go to Indexers", group: "Navigation" },
  { keys: ["g", "a"], label: "Go to Activity", group: "Navigation" },
  { keys: ["g", "c"], label: "Go to Calendar", group: "Navigation" },
  { keys: ["g", "s"], label: "Go to Settings", group: "Navigation" },
  { keys: ["g", "y"], label: "Go to System", group: "Navigation" },
  { keys: ["j"], label: "Focus next row", group: "Table" },
  { keys: ["k"], label: "Focus previous row", group: "Table" },
  { keys: ["x"], label: "Select focused row", group: "Table" },
  { keys: ["m"], label: "Toggle monitored", group: "Row" },
  { keys: ["."], label: "Open row actions", group: "Row" }
];

export function commandToShortcut(item: CommandItem): ShortcutItem | null {
  if (!item.shortcut) return null;
  return {
    keys: item.shortcut,
    label: item.label,
    group: item.group === "navigation" ? "Navigation" : "Actions"
  };
}

export const paletteIconFallback = Sparkles;
export const paletteTriggerIcon = Command;
export const searchIcon = Search;
export const downloadIcon = Download;
export const keyboardIcon = Keyboard;
