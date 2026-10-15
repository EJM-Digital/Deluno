import { useMemo, useState } from "react";
import { fetchJson, type TrackerDefinitionSummary } from "../../lib/api";
import { Button } from "../../components/ui/button";
import { cn } from "../../lib/utils";
import { Chip } from "../../components/ui/chip";
import { Field } from "../../components/ui/field";
import { Input } from "../../components/ui/input";
import { Select } from "../../components/ui/select";

import { SignedApiLink } from "../../components/app/signed-api-link";
export type TrackerKind = "all" | "public" | "semi-private" | "private";

export function privacyWords(privacy: string) {
  return privacy === "public" ? "Public" : privacy === "semi-private" ? "Semi-private" : "Private";
}

/** "zh-CN" as "Chinese (China)": the list is read by people, not by codes. */
export function languageName(code: string) {
  try {
    return new Intl.DisplayNames(["en"], { type: "language" }).of(code) ?? code;
  } catch {
    return code;
  }
}

/** Plain words for why a site cannot be used yet. */
export function unsupportedWords(reason: string) {
  return /captcha/i.test(reason) ? "Its sign-in needs a captcha picture solved by hand, which Deluno cannot do on its own." : reason;
}

/**
 * The picker's list: what matches the search words (in a site's name,
 * address, description or language), the type and the language, the ones
 * Deluno can run first and the ones it cannot after them, each in name order.
 */
export function filterTrackers(trackers: TrackerDefinitionSummary[], search: string, kind: TrackerKind, language: string) {
  const words = search.trim().toLowerCase().split(/\s+/).filter(Boolean);
  return trackers
    .filter((tracker) => kind === "all" || tracker.privacy === kind)
    .filter((tracker) => !language || tracker.language === language)
    .filter((tracker) => {
      const haystack = [tracker.name, tracker.id, tracker.description, languageName(tracker.language), tracker.links.join(" ")].join(" ").toLowerCase();
      return words.every((word) => haystack.includes(word));
    })
    .sort((left, right) => Number(left.unsupported != null) - Number(right.unsupported != null) || left.name.localeCompare(right.name));
}

const SHOWN_AT_MOST = 200;

interface SiteListRun {
  status: string;
  error: string | null;
  updatedTrackerIds: string[];
}

/**
 * The tracker site list is built in (#986); this only looks for newer site
 * files in the public Prowlarr repository, and only when pressed. The
 * scheduled check is a separate switch in Settings, General (off by default).
 */
export function SiteListPanel({ onDone }: { onDone: () => void }) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function run() {
    setBusy(true);
    setMessage(null);
    try {
      const result = await fetchJson<SiteListRun>("/api/trackers/definitions/update-check/run", { method: "POST" });
      if (result.status === "failed") {
        setMessage(result.error ?? "Deluno could not reach the public Prowlarr repository. Nothing was changed; try again in a moment.");
      } else {
        setMessage(result.updatedTrackerIds.length > 0 ? `Got ${result.updatedTrackerIds.length} newer site file${result.updatedTrackerIds.length === 1 ? "" : "s"}.` : "The site files are already up to date.");
        onDone();
      }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Deluno could not check for newer site files.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex flex-col gap-1.5 rounded-(--radius-control) border border-hairline p-3 text-(length:--type-body-sm)">
      <div className="flex items-center gap-2">
        <span className="min-w-0 flex-1 text-muted-foreground">
          Sites come from the public Prowlarr repository.{" "}
          <SignedApiLink className="font-medium text-primary hover:underline" path="/api/trackers/definitions/notice" mode="open" fallbackName="tracker-definitions-notice.md">Where they come from and under what terms</SignedApiLink>.
        </span>
        <Button type="button" size="sm" variant="secondary" disabled={busy} onClick={() => void run()}>
          {busy ? "Checking…" : "Check for newer site files"}
        </Button>
      </div>
      {message ? <p role="status" className="text-(length:--type-caption) text-muted-foreground">{message}</p> : null}
    </div>
  );
}

/** Every tracker Deluno knows, searchable by name, type and language, with the ones it cannot run listed and explained. */
export function TrackerPicker({ trackers, error, onPick, onRefresh }: { trackers: TrackerDefinitionSummary[]; error?: string; onPick: (tracker: TrackerDefinitionSummary) => void; onRefresh?: () => void }) {
  const [search, setSearch] = useState("");
  const [kind, setKind] = useState<TrackerKind>("all");
  const [language, setLanguage] = useState("");
  const languages = useMemo(
    () => [...new Set(trackers.map((tracker) => tracker.language))].map((code) => ({ value: code, label: languageName(code) })).sort((left, right) => left.label.localeCompare(right.label)),
    [trackers]
  );
  const shown = useMemo(() => filterTrackers(trackers, search, kind, language), [trackers, search, kind, language]);
  const runnable = trackers.filter((tracker) => !tracker.unsupported).length;
  const visible = shown.slice(0, SHOWN_AT_MOST);
  const firstUnsupported = visible.findIndex((tracker) => tracker.unsupported != null);

  return (
    <Field label="Tracker" error={error} help={`${runnable} of ${trackers.length} tracker sites Deluno can search itself. Any it cannot use yet are listed at the bottom, each with why.`}>
      <div className="flex flex-col gap-2">
        <SiteListPanel onDone={() => onRefresh?.()} />
        <Input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search by name, e.g. YTS or TorrentLeech" aria-label="Search trackers" autoComplete="off" />
        <div className="flex gap-2">
          <Select value={kind} onChange={(event) => setKind(event.target.value as TrackerKind)} aria-label="Which kind of tracker" options={[{ value: "all", label: "Any kind" }, { value: "public", label: "Public" }, { value: "semi-private", label: "Semi-private" }, { value: "private", label: "Private" }]} />
          <Select value={language} onChange={(event) => setLanguage(event.target.value)} aria-label="Which language" options={[{ value: "", label: "Any language" }, ...languages]} />
        </div>
        <ul role="listbox" aria-label="Trackers" className="max-h-72 overflow-y-auto rounded-(--radius-control) border border-hairline">
          {shown.length === 0 ? <li className="px-3 py-2 text-(length:--type-body-sm) text-muted-foreground">No tracker matches these words.</li> : null}
          {visible.map((tracker, index) => (
            <li key={tracker.id}>
              {index === firstUnsupported ? (
                <p className="border-b border-hairline bg-surface-2 px-3 py-1.5 text-(length:--type-caption) font-medium text-muted-foreground">Deluno cannot use these yet</p>
              ) : null}
              <button
                type="button"
                role="option"
                aria-selected={false}
                disabled={tracker.unsupported != null}
                onClick={() => onPick(tracker)}
                title={tracker.unsupported ?? tracker.description}
                className={cn("flex w-full flex-col gap-0.5 border-b border-hairline px-3 py-2 text-left text-(length:--type-body-sm) last:border-b-0", tracker.unsupported ? "cursor-not-allowed opacity-70" : "hover:bg-surface-2")}
              >
                <span className="flex w-full items-center gap-2">
                  <span className="min-w-0 flex-1 truncate font-medium text-foreground">{tracker.name}</span>
                  <span className="shrink-0 text-(length:--type-caption) text-muted-foreground">{[languageName(tracker.language), tracker.movies ? "Films" : null, tracker.tv ? "Series" : null].filter(Boolean).join(" · ")}</span>
                  <Chip tone={tracker.unsupported ? "idle" : tracker.privacy === "public" ? "info" : "warn"}>{privacyWords(tracker.privacy)}</Chip>
                </span>
                {tracker.unsupported ? <span className="text-(length:--type-caption) text-muted-foreground">{unsupportedWords(tracker.unsupported)}</span> : null}
              </button>
            </li>
          ))}
        </ul>
        {shown.length > SHOWN_AT_MOST ? <p className="text-(length:--type-caption) text-muted-foreground">Showing {SHOWN_AT_MOST} of {shown.length}. Type part of a name, or pick a kind or language, to narrow the list.</p> : null}
      </div>
    </Field>
  );
}
