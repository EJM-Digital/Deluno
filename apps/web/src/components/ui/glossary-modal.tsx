import { X, HelpCircle } from "lucide-react";
import { Button } from "./button";

interface GlossaryItem {
  term: string;
  definition: string;
}

const GLOSSARY: GlossaryItem[] = [
  {
    term: "Monitored",
    definition: "A title (movie or series) is marked for tracking. Deluno will search for new releases and automatically import matching content."
  },
  {
    term: "Wanted",
    definition: "A title needs a new release. This could be a missing episode, a movie not yet imported, or an upgrade opportunity."
  },
  {
    term: "Quality Cutoff",
    definition: "The minimum acceptable quality. Once a release at or above this quality is imported, Deluno stops searching for upgrades."
  },
  {
    term: "Custom Format",
    definition: "A rule that assigns bonus points to releases based on attributes (codec, audio, release group, source, etc.). Used to prefer specific qualities without hard blocking."
  },
  {
    term: "Custom Format Score",
    definition: "The total bonus points a release earns from matching custom format rules. Higher scores = better preferred release."
  },
  {
    term: "Quality Profile",
    definition: "A named configuration that combines a ranked list of qualities, a quality cutoff, and custom format rules. Assigned to each library."
  },
  {
    term: "Media Plan",
    definition: "Deluno's outcome-first home for what version you want: quality, file size, release preferences, language, and upgrades. Destination rules stay separate for folder exceptions."
  },
  {
    term: "Dry Run",
    definition: "A test mode where Deluno shows what it would grab or import without actually taking action. Useful for verifying automation rules."
  },
  {
    term: "Indexer",
    definition: "The technical name for a search source. Deluno queries indexers, Torznab/Newznab services, or RSS feeds for releases. People moving from Prowlarr can manage them under Search sources."
  },
  {
    term: "Search Source",
    definition: "The friendly name for an indexer or release-search service. Deluno tests it before saying it is ready to search."
  },
  {
    term: "Download Client",
    definition: "Software that Deluno sends releases to for downloading (qBittorrent, Transmission, SABnzbd, etc.). Must be configured before importing."
  },
  {
    term: "Transfers",
    definition: "The live handoff from an approved release through downloading, processing, import, and recovery. It combines the practical queue and import work without hiding the history in Activity."
  },
  {
    term: "Live Automation",
    definition: "The operational view of what Deluno is searching, retrying, waiting on, or pausing right now. It is separate from Library setup so a live action is not mistaken for a permanent configuration change."
  },
  {
    term: "Download Health & Cleanup",
    definition: "Deluno's integrated replacement for a separate cleanup tool: it explains unhealthy downloads, previews a safe action, and can schedule a bounded replacement search. It is not antivirus protection and never silently deletes unproven shared data."
  },
  {
    term: "Guide-backed Plan",
    definition: "A plan informed by a quality guide or imported configuration. Deluno must show its source, effective rules, and any overrides rather than claiming every Recyclarr or Configarr setting maps exactly."
  },
  {
    term: "Monitored Episode",
    definition: "A single TV episode marked for watching. Deluno will search for and import releases only for monitored episodes, not entire seasons."
  }
];

interface GlossaryModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function GlossaryModal({ open, onOpenChange }: GlossaryModalProps) {
  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div
        className="fixed inset-0 bg-black/50 backdrop-blur-[2px]"
        onClick={() => onOpenChange(false)}
      />
      <div className="relative z-50 w-full max-w-2xl max-h-[90dvh] rounded-2xl border border-hairline bg-card shadow-2xl overflow-hidden flex flex-col">
        <div className="flex items-center justify-between border-b border-hairline p-6">
          <div className="flex items-center gap-2">
            <HelpCircle className="h-5 w-5 text-primary" />
            <h2 className="text-lg font-semibold">Glossary</h2>
          </div>
          <button
            onClick={() => onOpenChange(false)}
            className="rounded-lg p-1.5 text-muted-foreground hover:bg-secondary hover:text-foreground transition-colors"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="overflow-auto flex-1">
          <div className="space-y-[var(--page-gap)] p-6">
            {GLOSSARY.map((item) => (
              <div key={item.term} className="space-y-1.5">
                <p className="font-semibold text-foreground">{item.term}</p>
                <p className="text-sm text-muted-foreground leading-relaxed">
                  {item.definition}
                </p>
              </div>
            ))}
          </div>
        </div>

        <div className="border-t border-hairline p-4 flex justify-end">
          <Button variant="outline" size="sm" onClick={() => onOpenChange(false)}>
            Close
          </Button>
        </div>
      </div>
    </div>
  );
}
