import { BookOpenText, Brain, DownloadCloud, FolderTree, KeyRound, Route, ShieldCheck, Sparkles, WandSparkles } from "lucide-react";
import { Link } from "react-router-dom";
import { Button } from "../components/ui/button";
import { ListCard, ListCell, ListNameCell, ListRow, ListTable } from "../components/ui/list-card";
import { Badge } from "../components/ui/badge";

const workflowSections = [
  {
    icon: Sparkles,
    title: "Beginner Setup",
    badge: "Start here",
    body:
      "Create the first user, run guided setup, choose roots, pick a simple quality target, connect providers if you have them, then add the first title. Advanced controls stay available after the baseline works."
  },
  {
    icon: Route,
    title: "Routing and Tags",
    badge: "Better than extra instances",
    body:
      "Deluno separates Movies and TV by media type, library, category, destination rule, and dispatch metadata. Tags are optional policy labels for kids, 4K, foreign-language media, processor workflows, or special root folders."
  },
  {
    icon: DownloadCloud,
    title: "External Download Clients",
    badge: "Recommended",
    body:
      "Use qBittorrent, SABnzbd, NZBGet, Deluge, Transmission, or uTorrent-style Web UI clients for transfer work. Deluno normalizes queue, history, progress, speed, ETA, errors, and import readiness."
  },
  {
    icon: Brain,
    title: "Release Decisions",
    badge: "Safety first",
    body:
      "Search decisions should explain the quality outcome, cutoff, typed release preferences, release group, estimated bitrate, size sanity, seeders, indexer health, language expectations, and never-grab rules. Any legacy scoring values are provenance only. Force override is available but audited."
  },
  {
    icon: WandSparkles,
    title: "Refine Before Import",
    badge: "Processor workflow",
    body:
      "A library can wait for an external refiner or another processor to clean audio/subtitles before Deluno imports, creates single-copy links (hardlinks) or moves, renames, refreshes metadata, and records recovery actions if processing stalls."
  },
  {
    icon: FolderTree,
    title: "Import Hygiene",
    badge: "Clean library",
    body:
      "Imports should preview destination, validate with ffprobe when available, avoid duplicates, preserve single-copy links where requested, and keep Movies and TV in their correct folders without user guesswork."
  },
  {
    icon: ShieldCheck,
    title: "Metadata",
    badge: "Provider-backed",
    body:
      "Metadata powers title matching, posters, backdrops, ratings, genres, dates, and naming context. Deluno manages title matching during installation; normal users never need to supply personal provider keys."
  },
  {
    icon: KeyRound,
    title: "API Access",
    badge: "Integrations",
    body:
      "Generate keys in System -> API. External apps should read the manifest first, then health, queue, activity, import preview, and processor-event endpoints depending on their role."
  }
] as const;

const lifecycle = [
  "Title is added from metadata search, list import, or manual entry.",
  "Policy picks library, root folder, quality target, custom formats, and search sources.",
  "Search decision engine compares releases and blocks unsafe matches instead of blindly upgrading.",
  "Approved release is sent to the chosen external download client with category context.",
  "Queue telemetry tracks progress, speed, ETA, and failure state in one normalized model.",
  "Completed download is imported directly or held for processor output, depending on library workflow.",
  "Deluno validates, moves or creates single-copy links (hardlinks), renames, refreshes metadata, and records an audit trail."
];

export function SystemDocsPage() {
  return (
    <div className="space-y-[var(--page-gap)]">
      <ListCard title={<span className="flex items-center gap-2"><BookOpenText className="h-5 w-5 text-primary" />
            Deluno Operating Model</span>} count={<>This is the plain-English guide for how Deluno should work once a user has moved past first setup.</>}>
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)] md:grid-cols-2 xl:grid-cols-4">
          {workflowSections.map((section) => {
            const Icon = section.icon;
            return (
              <div key={section.title} className="rounded-2xl border border-hairline bg-surface-1 p-[var(--tile-pad)]">
                <div className="flex items-start justify-between gap-3">
                  <span className="flex h-10 w-10 items-center justify-center rounded-xl border border-primary/20 bg-primary/10 text-primary">
                    <Icon className="h-5 w-5" />
                  </span>
                  <Badge variant="default">{section.badge}</Badge>
                </div>
                <p className="mt-4 font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
                  {section.title}
                </p>
                <p className="mt-2 density-help leading-relaxed text-muted-foreground">{section.body}</p>
              </div>
            );
          })}
        </div>
      </ListCard>

      <ListCard title="Start over from the beginning" count="The guided setup walks the whole first run again — it never overwrites what you already have">
        <ListTable columns={[{ label: "Guide" }, { label: "What it covers", width: "minmax(0,2fr)" }, { label: "Open", width: "140px", mobile: true, srOnly: true }]} chevron={false}>
          <ListRow>
            <ListNameCell name="Guided setup" sub="First run" />
            <ListCell primary="Libraries, folders, a quality target, release preferences, and optionally a search source and download client." />
            <ListCell mobile align="end">
              <Button asChild type="button" variant="outline" size="sm">
                <Link to="/setup-guide">Open</Link>
              </Button>
            </ListCell>
          </ListRow>
        </ListTable>
      </ListCard>

      <ListCard title={<span className="flex items-center gap-2">Media Lifecycle</span>} count={<>Every major screen and automation should support this sequence without making the user guess.</>}>
        <div className="p-[var(--card-pad-x)]">
          <div className="grid gap-3 lg:grid-cols-7">
            {lifecycle.map((item, index) => (
              <div key={item} className="rounded-2xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.75)]">
                <span className="text-[length:var(--type-caption)] font-bold tabular-nums text-primary">{String(index + 1).padStart(2, "0")}</span>
                <p className="mt-2 text-[length:var(--type-body)] font-medium leading-snug text-foreground">{item}</p>
              </div>
            ))}
          </div>
        </div>
      </ListCard>
    </div>
  );
}
