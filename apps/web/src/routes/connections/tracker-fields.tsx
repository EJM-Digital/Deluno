import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { fetchJson, type IndexerItem, type TrackerDefinitionSummary, type TrackerSettingSummary } from "../../lib/api";
import { cn } from "../../lib/utils";
import { Chip } from "../../components/ui/chip";
import { Field } from "../../components/ui/field";
import { Input } from "../../components/ui/input";
import { Select } from "../../components/ui/select";
import { SwitchRow } from "../../components/ui/switch";
import type { IndexerForm } from "./forms";

/** Every tracker Deluno can search, fetched once and kept: the list only changes with a Deluno update. */
export function useTrackers(enabled: boolean) {
  return useQuery({ queryKey: ["trackers"], queryFn: () => fetchJson<TrackerDefinitionSummary[]>("/api/trackers"), enabled, staleTime: Infinity });
}

/**
 * Which tracker a built-in tracker indexer is, and its own settings (#606).
 *
 * Picking one fills in what the definition already knows (its address, name
 * and whether it has films or series), so the only questions left are the
 * ones the site asks: a sign-in, a cookie, a key, its own options.
 */
export function TrackerFields({
  form,
  setForm,
  editing,
  errors,
  clearError
}: {
  form: IndexerForm;
  setForm: (updater: (current: IndexerForm) => IndexerForm) => void;
  editing: IndexerItem | null;
  errors: Record<string, string>;
  clearError: (key: string) => void;
}) {
  const trackers = useTrackers(true);
  const chosen = trackers.data?.find((item) => item.id === form.definitionId) ?? null;

  if (trackers.isLoading) {
    return <p className="flex items-center gap-2 text-[length:var(--type-body-sm)] text-muted-foreground"><Loader2 className="h-3.5 w-3.5 animate-spin" />Loading the trackers Deluno knows…</p>;
  }

  if (!chosen) {
    return (
      <TrackerPicker
        trackers={trackers.data ?? []}
        error={errors.definitionId}
        onPick={(tracker) => {
          clearError("definitionId");
          clearError("baseUrl");
          setForm((current) => ({
            ...current,
            definitionId: tracker.id,
            name: current.name.trim() ? current.name : tracker.name,
            baseUrl: tracker.links[0] ?? "",
            scope: tracker.movies && !tracker.tv ? "movies" : tracker.tv && !tracker.movies ? "tv" : "both",
            trackerSettings: Object.fromEntries(tracker.settings.filter((setting) => !setting.secret && setting.default != null && isEditable(setting)).map((setting) => [setting.name, setting.default!])),
            trackerSecrets: {},
            // A site that wants a ratio is asked about sharing straight away.
            sharingRule: tracker.privacy === "public" ? current.sharingRule : "strict"
          }));
        }}
      />
    );
  }

  const secretsSet = new Set(editing?.definitionSecretsSet ?? []);
  return (
    <>
      <div className="flex flex-wrap items-center gap-2 text-[length:var(--type-body-sm)]">
        <span className="font-medium text-foreground">{chosen.name}</span>
        <Chip tone={chosen.privacy === "public" ? "info" : "warn"}>{privacyWords(chosen.privacy)}</Chip>
        <span className="text-muted-foreground">{chosen.language}</span>
        {editing ? null : (
          <button type="button" className="ml-auto text-[length:var(--type-caption)] font-medium text-primary hover:underline" onClick={() => setForm((current) => ({ ...current, definitionId: "", trackerSettings: {}, trackerSecrets: {} }))}>
            Choose another
          </button>
        )}
      </div>
      {chosen.description ? <p className="text-[length:var(--type-caption)] text-muted-foreground">{chosen.description}</p> : null}
      {chosen.mayNeedCloudflareHelp ? (
        <p className="text-[length:var(--type-caption)] text-warning">This site sometimes puts a Cloudflare check in front of searches. Deluno gets past it through a Cloudflare helper (FlareSolverr or Byparr) set under Settings, General. Without one, try another of its addresses.</p>
      ) : null}
      {chosen.links.length > 1 ? (
        <Field label="Address" error={errors.baseUrl} help="The site's addresses change; Deluno tries the others if this one does not answer.">
          <Select value={form.baseUrl} onChange={(event) => setForm((current) => ({ ...current, baseUrl: event.target.value }))} options={withCurrent(chosen.links, form.baseUrl).map((link) => ({ value: link, label: link }))} />
        </Field>
      ) : null}
      {chosen.settings.filter(isEditable).map((setting) => (
        <TrackerSettingField
          key={setting.name}
          setting={setting}
          value={setting.secret ? form.trackerSecrets[setting.name] ?? "" : form.trackerSettings[setting.name] ?? setting.default ?? ""}
          saved={setting.secret && secretsSet.has(setting.name)}
          onChange={(value) => setForm((current) => setting.secret
            ? { ...current, trackerSecrets: { ...current.trackerSecrets, [setting.name]: value } }
            : { ...current, trackerSettings: { ...current.trackerSettings, [setting.name]: value } })}
        />
      ))}
    </>
  );
}

function TrackerPicker({ trackers, error, onPick }: { trackers: TrackerDefinitionSummary[]; error?: string; onPick: (tracker: TrackerDefinitionSummary) => void }) {
  const [search, setSearch] = useState("");
  const [kind, setKind] = useState<"all" | "public" | "private">("all");
  const shown = useMemo(() => {
    const words = search.trim().toLowerCase();
    return trackers
      .filter((tracker) => kind === "all" || (kind === "public" ? tracker.privacy === "public" : tracker.privacy !== "public"))
      .filter((tracker) => !words || tracker.name.toLowerCase().includes(words) || tracker.id.includes(words) || tracker.description.toLowerCase().includes(words))
      // The ones Deluno can run first; the rest stay listed, with the reason.
      .sort((left, right) => Number(left.unsupported != null) - Number(right.unsupported != null) || left.name.localeCompare(right.name));
  }, [trackers, search, kind]);
  const runnable = trackers.filter((tracker) => !tracker.unsupported).length;

  return (
    <Field label="Tracker" error={error} help={`${runnable} trackers Deluno searches itself, from Deluno's own definitions.`}>
      <div className="flex flex-col gap-2">
        <div className="flex gap-2">
          <Input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search by name, e.g. YTS or TorrentLeech" aria-label="Search trackers" autoComplete="off" />
          <Select className="w-40" value={kind} onChange={(event) => setKind(event.target.value as typeof kind)} aria-label="Which trackers" options={[{ value: "all", label: "All" }, { value: "public", label: "Public" }, { value: "private", label: "Private" }]} />
        </div>
        <ul role="listbox" aria-label="Trackers" className="max-h-72 overflow-y-auto rounded-[var(--radius-control)] border border-hairline">
          {shown.length === 0 ? <li className="px-3 py-2 text-[length:var(--type-body-sm)] text-muted-foreground">No tracker matches “{search}”.</li> : null}
          {shown.slice(0, 200).map((tracker) => (
            <li key={tracker.id}>
              <button
                type="button"
                role="option"
                aria-selected={false}
                disabled={tracker.unsupported != null}
                onClick={() => onPick(tracker)}
                title={tracker.unsupported ?? tracker.description}
                className={cn("flex w-full items-center gap-2 border-b border-hairline px-3 py-2 text-left text-[length:var(--type-body-sm)] last:border-b-0", tracker.unsupported ? "cursor-not-allowed opacity-60" : "hover:bg-surface-2")}
              >
                <span className="min-w-0 flex-1 truncate font-medium text-foreground">{tracker.name}</span>
                <span className="shrink-0 text-[length:var(--type-caption)] text-muted-foreground">{[tracker.movies ? "Films" : null, tracker.tv ? "Series" : null].filter(Boolean).join(" · ") || "Other"}</span>
                <Chip tone={tracker.unsupported ? "idle" : tracker.privacy === "public" ? "info" : "warn"}>{tracker.unsupported ? (/captcha/i.test(tracker.unsupported) ? "Needs a captcha" : "Not supported") : privacyWords(tracker.privacy)}</Chip>
              </button>
            </li>
          ))}
        </ul>
        {shown.length > 200 ? <p className="text-[length:var(--type-caption)] text-muted-foreground">Showing 200 of {shown.length}. Type part of a name to narrow the list.</p> : null}
      </div>
    </Field>
  );
}

function TrackerSettingField({ setting, value, saved, onChange }: { setting: TrackerSettingSummary; value: string; saved: boolean; onChange: (value: string) => void }) {
  if (setting.type === "info") {
    return setting.default ? <p className="text-[length:var(--type-caption)] text-muted-foreground">{plainText(setting.default)}</p> : null;
  }

  if (setting.type === "checkbox") {
    return <SwitchRow label={setting.label} checked={value.toLowerCase() === "true"} onCheckedChange={(checked) => onChange(checked ? "true" : "false")} />;
  }

  if (setting.type === "select" && setting.options.length > 0) {
    return (
      <Field label={setting.label}>
        <Select value={value} onChange={(event) => onChange(event.target.value)} options={setting.options.map((option) => ({ value: option.key, label: option.value }))} />
      </Field>
    );
  }

  return (
    <Field label={setting.label} help={setting.secret ? (saved ? "Saved and encrypted. Leave empty to keep it; type a new one to replace it." : "Stored encrypted, and never shown again.") : undefined}>
      <Input
        type={setting.secret ? "password" : "text"}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={setting.secret && saved ? "••••••••  (unchanged)" : undefined}
        autoComplete="off"
        spellCheck={false}
      />
    </Field>
  );
}

/** Settings a person answers. Multi-selects are left at the tracker's defaults for now. */
function isEditable(setting: TrackerSettingSummary) {
  return ["text", "password", "checkbox", "select", "info"].includes(setting.type);
}

function privacyWords(privacy: string) {
  return privacy === "public" ? "Public" : privacy === "semi-private" ? "Semi-private" : "Private";
}

/** The definitions' notes carry a little HTML; the words are what matter. */
function plainText(html: string) {
  return html.replace(/<br\s*\/?>/gi, " ").replace(/<[^>]+>/g, "").replace(/&amp;/g, "&").replace(/\s+/g, " ").trim();
}

function withCurrent(links: string[], current: string) {
  return current && !links.includes(current) ? [current, ...links] : links;
}
