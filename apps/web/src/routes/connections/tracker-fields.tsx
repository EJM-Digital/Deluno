import { useQuery } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { fetchJson, type IndexerItem, type TrackerDefinitionSummary, type TrackerSettingSummary } from "../../lib/api";
import { TrackerPicker, languageName, privacyWords } from "./tracker-picker";
import { Chip } from "../../components/ui/chip";
import { Field } from "../../components/ui/field";
import { Input } from "../../components/ui/input";
import { Select } from "../../components/ui/select";
import { SwitchRow } from "../../components/ui/switch";
import type { IndexerForm } from "./forms";
import { PreferencesLink } from "./preferences-link";
import { plainText } from "./plain-text";

/** Every tracker Deluno can search, fetched once and kept: the list only changes with a Deluno release or a definitions update. */
export function useTrackers(enabled: boolean) {
  return useQuery({ queryKey: ["trackers"], queryFn: () => fetchJson<TrackerDefinitionSummary[]>("/api/trackers"), enabled, staleTime: 60_000 });
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
  clearError,
  cloudflareExplained = false
}: {
  form: IndexerForm;
  setForm: (updater: (current: IndexerForm) => IndexerForm) => void;
  editing: IndexerItem | null;
  errors: Record<string, string>;
  clearError: (key: string) => void;
  /** A message beside this already says what a Cloudflare check needs. */
  cloudflareExplained?: boolean;
}) {
  const trackers = useTrackers(true);
  const chosen = trackers.data?.find((item) => item.id === form.definitionId) ?? null;

  if (trackers.isLoading) {
    return <p className="flex items-center gap-2 text-(length:--type-body-sm) text-muted-foreground"><Loader2 className="h-3.5 w-3.5 animate-spin" />Loading the trackers Deluno knows…</p>;
  }

  if (!chosen) {
    return (
      <TrackerPicker
        trackers={trackers.data ?? []}
        onRefresh={() => void trackers.refetch()}
        error={errors.definitionId}
        onPick={(tracker) => {
          clearError("definitionId");
          clearError("baseUrl");
          setForm((current) => ({
            ...current,
            definitionId: tracker.id,
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
      <div className="flex flex-wrap items-center gap-2 text-(length:--type-body-sm)">
        <span className="font-medium text-foreground">{chosen.name}</span>
        <Chip tone={chosen.privacy === "public" ? "info" : "warn"}>{privacyWords(chosen.privacy)}</Chip>
        <span className="text-muted-foreground">{languageName(chosen.language)}</span>
        {editing ? null : (
          <button type="button" className="ml-auto text-(length:--type-caption) font-medium text-primary hover:underline" onClick={() => setForm((current) => ({ ...current, definitionId: "", trackerSettings: {}, trackerSecrets: {} }))}>
            Choose another
          </button>
        )}
      </div>
      {chosen.description ? <p className="text-(length:--type-caption) text-muted-foreground">{chosen.description}</p> : null}
      {chosen.mayNeedCloudflareHelp && !cloudflareExplained ? (
        <p className="text-(length:--type-caption) text-warning">This site sometimes puts a Cloudflare check in front of searches. Deluno gets past it through a Cloudflare helper (FlareSolverr or Byparr), set once for every tracker in Preferences, General. <PreferencesLink />. Without one, try another of its addresses.</p>
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

function TrackerSettingField({ setting, value, saved, onChange }: { setting: TrackerSettingSummary; value: string; saved: boolean; onChange: (value: string) => void }) {
  if (setting.type === "info") {
    return setting.default ? <p className="text-(length:--type-caption) text-muted-foreground">{plainText(setting.default)}</p> : null;
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

function withCurrent(links: string[], current: string) {
  return current && !links.includes(current) ? [current, ...links] : links;
}
