import { BookOpenText, Brain, DownloadCloud, FolderTree, KeyRound, Route, ShieldCheck, Sparkles, WandSparkles } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Badge } from "../components/ui/badge";

const workflowSections = [
  {
    icon: Sparkles,
    title: "Beginner setup",
    badge: "Start here",
    body:
      "Create the first user, run guided setup, choose roots, pick a simple quality target, connect providers if you have them, then add the first title. Advanced controls stay available after the baseline works."
  },
  {
    icon: Route,
    title: "Routing and tags",
    badge: "Better than extra instances",
    body:
      "Deluno separates Movies and TV by media type, library, category, destination rule, and dispatch metadata. Tags are optional policy labels for anime, kids, 4K, foreign-language media, processor workflows, or special root folders."
  },
  {
    icon: DownloadCloud,
    title: "External download clients",
    badge: "Recommended",
    body:
      "Use qBittorrent, SABnzbd, NZBGet, Deluge, Transmission, or uTorrent-style Web UI clients for transfer work. Deluno normalizes queue, history, progress, speed, ETA, errors, and import readiness."
  },
  {
    icon: Brain,
    title: "Release scoring",
    badge: "Safety first",
    body:
      "Search decisions should explain quality delta, cutoff, custom formats, release group, estimated bitrate, size sanity, seeders, indexer health, language expectations, and never-grab rules. Force override is available but audited."
  },
  {
    icon: WandSparkles,
    title: "Refine before import",
    badge: "Processor workflow",
    body:
      "A library can wait for an external refiner or another processor to clean audio/subtitles before Deluno imports, hardlinks or moves, renames, refreshes metadata, and records recovery actions if processing stalls."
  },
  {
    icon: FolderTree,
    title: "Import hygiene",
    badge: "Clean library",
    body:
      "Imports should preview destination, validate with ffprobe when available, avoid duplicates, preserve hardlinks where requested, and keep Movies and TV in their correct folders without user guesswork."
  },
  {
    icon: ShieldCheck,
    title: "Metadata",
    badge: "Provider-backed",
    body:
      "Metadata powers title matching, posters, backdrops, ratings, genres, dates, and naming context. Direct TMDb/OMDb keys work now; a hosted broker can later remove API-key friction for normal users."
  },
  {
    icon: KeyRound,
    title: "API access",
    badge: "Integrations",
    body:
      "Generate keys in System -> API. External apps should read the manifest first, then health, queue, activity, import preview, and processor-event endpoints depending on their role."
  }
] as const;

const lifecycle = [
  "Title is added from metadata search, list import, or manual entry.",
  "Policy picks library, root folder, quality target, custom formats, and search sources.",
  "Search scorer ranks releases and blocks unsafe matches instead of blindly upgrading.",
  "Approved release is sent to the chosen external download client with category context.",
  "Queue telemetry tracks progress, speed, ETA, and failure state in one normalized model.",
  "Completed download is imported directly or held for processor output, depending on library workflow.",
  "Deluno validates, moves or hardlinks, renames, refreshes metadata, and records an audit trail."
];

export function SystemDocsPage() {
  return (
    <div className="space-y-[var(--page-gap)]">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <BookOpenText className="h-5 w-5 text-primary" />
            Deluno operating model
          </CardTitle>
          <CardDescription>
            This is the plain-English guide for how Deluno should work once a user has moved past first setup.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-[var(--grid-gap)] md:grid-cols-2 xl:grid-cols-4">
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
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Media lifecycle</CardTitle>
          <CardDescription>Every major screen and automation should support this sequence without making the user guess.</CardDescription>
        </CardHeader>
        <CardContent>
          <div className="grid gap-3 lg:grid-cols-7">
            {lifecycle.map((item, index) => (
              <div key={item} className="rounded-2xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.75)]">
                <span className="font-mono text-[length:var(--type-caption)] font-bold text-primary">{String(index + 1).padStart(2, "0")}</span>
                <p className="mt-2 text-[length:var(--type-body)] font-medium leading-snug text-foreground">{item}</p>
              </div>
            ))}
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
