import { useEffect, useState } from "react";
import { TagPicker } from "../../components/app/tag-picker";
import { Loader2, Plus, Trash2 } from "lucide-react";
import { fetchJson, type DownloadClientItem, type DownloadClientPathMappingItem, type QbittorrentBlocklistSyncState, type SharingRuleItem } from "../../lib/api";
import { Button } from "../../components/ui/button";
import { Chip } from "../../components/ui/chip";
import { Disclosure } from "../../components/ui/disclosure";
import { DrawerDanger, DrawerSection } from "../../components/ui/drawer";
import { Field, FieldRow } from "../../components/ui/field";
import { Input } from "../../components/ui/input";
import { PathInput } from "../../components/ui/path-input";
import { PresetField } from "../../components/ui/preset-field";
import { Select } from "../../components/ui/select";
import { SwitchRow } from "../../components/ui/switch";
import { CATEGORY_OPTIONS, CLIENT_PRESETS, HOST_OPTIONS, PORT_OPTIONS, TV_CATEGORY_OPTIONS } from "./presets";
import type { ClientForm } from "./forms";
import { healthChip, relative } from "./format";
export function ClientDrawerBody({
  form,
  setForm,
  editing,
  errors,
  clearError,
  mappings,
  newMapping,
  setNewMapping,
  busy,
  onAddMapping,
  onRemoveMapping,
  onTestMapping,
  onRemove,
  blocklistSync
}: {
  form: ClientForm;
  setForm: (updater: (current: ClientForm) => ClientForm) => void;
  editing: DownloadClientItem | null;
  errors: Record<string, string>;
  clearError: (key: string) => void;
  mappings: DownloadClientPathMappingItem[];
  newMapping: { remotePath: string; localPath: string };
  setNewMapping: (value: { remotePath: string; localPath: string }) => void;
  busy: string | null;
  onAddMapping: () => void;
  onRemoveMapping: (mappingId: string) => void;
  onTestMapping: (mappingId: string) => void;
  onRemove: () => void;
  /** #822: this client's last content-blocklist "Excluded file names" sync attempt, or null when it has never run. */
  blocklistSync?: QbittorrentBlocklistSyncState | null;
}) {
  const [pathMappingOpen, setPathMappingOpen] = useState(false);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const preset = CLIENT_PRESETS.find((item) => item.protocol === form.protocol);
  /**
   * A client saved by an older Deluno can carry a protocol nothing can dispatch
   * to — "torrent", "usenet", "custom". The connection test now says so and
   * tells the reader to change it (#292), so the control that changes it has to
   * be reachable: locking the picker on every existing client left that
   * instruction impossible to follow. The picker also has to admit what is
   * stored, because a Select whose value matches no option silently displays
   * the first one, which had this reading "qBittorrent" while the client was a
   * 'torrent' client and could receive nothing.
   */
  const unusableProtocol = Boolean(editing) && !CLIENT_PRESETS.some((item) => item.protocol === form.protocol);
  const clientOptions = [
    ...(unusableProtocol ? [{ value: form.protocol, label: `${form.protocol} — Deluno cannot send to this` }] : []),
    ...CLIENT_PRESETS.map((item) => ({ value: item.protocol, label: `${item.label} · ${item.kind}` }))
  ];
  const chip = editing ? healthChip(editing) : null;
  const sameCategory = form.moviesCategory && form.moviesCategory === form.tvCategory;

  useEffect(() => {
    setPathMappingOpen(false);
  }, [editing?.id]);

  function choosePreset(protocol: string) {
    const next = CLIENT_PRESETS.find((item) => item.protocol === protocol);
    if (!next) return;
    setForm((current) => ({
      ...current,
      protocol,
      port: String(next.defaultPort),
      moviesCategory: next.defaultMoviesCategory,
      tvCategory: next.defaultTvCategory,
      name: current.name.trim() && !CLIENT_PRESETS.some((item) => item.label === current.name) ? current.name : next.label,
      // #980: switching to a preset that has a URL base of its own (rTorrent's
      // /RPC2) pre-fills it, the way the port and categories already do. Only
      // when the field is still blank, so picking a preset never overwrites a
      // URL base the person already typed.
      urlBase: current.urlBase.trim() ? current.urlBase : next.defaultUrlBase
    }));
  }

  return (
    <>
      <DrawerSection title="Basics">
        <FieldRow>
          <Field label="Name" error={errors.name}>
            <Input value={form.name} onChange={(event) => { clearError("name"); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder={preset?.label ?? "Download client"} autoComplete="off" />
          </Field>
          <Field label="Client" help={unusableProtocol ? "Deluno cannot send downloads to this kind of client. Pick the one you actually run." : editing ? undefined : preset?.setupHint}>
            <Select value={form.protocol} disabled={Boolean(editing) && !unusableProtocol} onChange={(event) => choosePreset(event.target.value)} options={clientOptions} />
          </Field>
        </FieldRow>
        <FieldRow>
          <Field label="Host or IP" error={errors.host}>
            <PresetField value={form.host} onChange={(value) => { clearError("host"); setForm((current) => ({ ...current, host: value })); }} options={HOST_OPTIONS} customLabel="Custom host / IP" customPlaceholder="Hostname or IP address" />
          </Field>
          <Field label="Port" error={errors.port}>
            <PresetField inputType="number" value={form.port} onChange={(value) => { clearError("port"); setForm((current) => ({ ...current, port: value })); }} options={PORT_OPTIONS} customLabel="Custom port" customPlaceholder="Port number" />
          </Field>
        </FieldRow>
        <SwitchRow label="Use SSL" description="Connect to this client using https instead of http." checked={form.useSsl} onCheckedChange={(checked) => setForm((current) => ({ ...current, useSsl: checked }))} />
        <FieldRow>
          <Field label="Username" optional>
            <Input value={form.username} onChange={(event) => setForm((current) => ({ ...current, username: event.target.value }))} autoComplete="off" />
          </Field>
          <Field label={preset?.authMode === "API key" ? "API key" : "Password"} optional help={editing ? "Stored encrypted. Leave empty to keep the current one." : preset?.authMode === "API key" ? "From the client's settings page." : undefined}>
            <Input type="password" value={form.password} onChange={(event) => setForm((current) => ({ ...current, password: event.target.value }))} placeholder={editing ? "••••••••  (unchanged)" : ""} autoComplete="new-password" />
          </Field>
        </FieldRow>
        <SwitchRow label="Enabled" description="Deluno may send approved releases to this client." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
        <SwitchRow label="Add downloads paused" description="New downloads sent to this client start paused instead of active." checked={form.addPaused} onCheckedChange={(checked) => setForm((current) => ({ ...current, addPaused: checked }))} />
      </DrawerSection>

      <DrawerSection title="Categories" aside="keep Movies and TV apart in the client">
        <FieldRow>
          <Field label="Movies category" help="The label or category your client uses for movie downloads.">
            <PresetField value={form.moviesCategory} onChange={(value) => setForm((current) => ({ ...current, moviesCategory: value }))} options={CATEGORY_OPTIONS} customLabel="Custom movie category" customPlaceholder="Download-client category" />
          </Field>
          <Field label="TV category" help={sameCategory ? undefined : "Should differ from the movies category."} error={sameCategory ? "Same as movies — downloads will mix in the client." : undefined}>
            <PresetField value={form.tvCategory} onChange={(value) => setForm((current) => ({ ...current, tvCategory: value }))} options={TV_CATEGORY_OPTIONS} customLabel="Custom TV category" customPlaceholder="Download-client category" />
          </Field>
        </FieldRow>
      </DrawerSection>

      <DrawerSection title="Completed Download Handling" aside="off unless you turn it on for this client">
        <SwitchRow
          label="Remove completed"
          description="Once Deluno imports a download from this client successfully, remove it from the client's queue."
          checked={form.removeCompleted}
          onCheckedChange={(checked) => setForm((current) => ({ ...current, removeCompleted: checked }))}
        />
        <SwitchRow
          label="Remove failed"
          description="Once a download from this client is recorded as failed, remove it from the client's queue. Separate from whether the release is blocked and searched again — that is decided by the import failure rules, not by this switch."
          checked={form.removeFailed}
          onCheckedChange={(checked) => setForm((current) => ({ ...current, removeFailed: checked }))}
        />
      </DrawerSection>

      {form.protocol === "qbittorrent" ? (
        <DrawerSection title="qBittorrent options" aside="add-time options and this client's own share limits">
          <SwitchRow
            label="Skip hash check"
            description="Skip qBittorrent's own hash check on a new grab. Only useful if you already trust the data is correct."
            checked={form.skipHashCheck}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, skipHashCheck: checked }))}
          />
          <SwitchRow
            label="Sequential download"
            description="Download pieces in order rather than rarest-first, so the file plays back from the start sooner."
            checked={form.sequentialDownload}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, sequentialDownload: checked }))}
          />
          <SwitchRow
            label="Prioritize first and last pieces"
            description="Fetch the first and last pieces first, so a video can be previewed before it finishes."
            checked={form.firstLastPiecePrio}
            onCheckedChange={(checked) => setForm((current) => ({ ...current, firstLastPiecePrio: checked }))}
          />
          <FieldRow>
            <Field label="Seed ratio limit" optional error={errors.shareRatioLimit} help="Leave blank to use qBittorrent's own default.">
              <Input inputMode="decimal" value={form.shareRatioLimit} onChange={(event) => { clearError("shareRatioLimit"); setForm((current) => ({ ...current, shareRatioLimit: event.target.value })); }} placeholder="e.g. 2.0" />
            </Field>
            <Field label="Seed time limit (minutes)" optional error={errors.shareSeedingTimeLimitMinutes} help="Leave blank to use qBittorrent's own default.">
              <Input inputMode="numeric" value={form.shareSeedingTimeLimitMinutes} onChange={(event) => { clearError("shareSeedingTimeLimitMinutes"); setForm((current) => ({ ...current, shareSeedingTimeLimitMinutes: event.target.value })); }} placeholder="e.g. 4320" />
            </Field>
          </FieldRow>
          <Field label="Inactive seed time limit (minutes)" optional error={errors.shareInactiveSeedingTimeLimitMinutes} help="Stop seeding after this many minutes with no upload activity, regardless of ratio or total seeding time. Leave blank to use qBittorrent's own default.">
            <Input inputMode="numeric" value={form.shareInactiveSeedingTimeLimitMinutes} onChange={(event) => { clearError("shareInactiveSeedingTimeLimitMinutes"); setForm((current) => ({ ...current, shareInactiveSeedingTimeLimitMinutes: event.target.value })); }} placeholder="e.g. 60" />
          </Field>
          <Field label="Save path" optional help="Where qBittorrent should save this client's downloads. Leave blank to use qBittorrent's own default, or its category's save path.">
            <PathInput value={form.location} onChange={(value) => setForm((current) => ({ ...current, location: value }))} placeholder="e.g. D:\Downloads\Movies" />
          </Field>
          {editing ? (
            <div className="flex items-start justify-between gap-3 rounded-[10px] border border-hairline px-[var(--field-pad-x)] py-2">
              <div className="min-w-0">
                <p className="text-[length:var(--type-body-sm)] text-foreground">Content blocklist sync</p>
                <p className="mt-0.5 text-[length:var(--type-caption)] text-muted-foreground">
                  {blocklistSync
                    ? blocklistSync.succeeded
                      ? (blocklistSync.message ?? "Last push succeeded.")
                      : `Last push failed: ${blocklistSync.message ?? "no reason given"}.`
                    : "Not synced yet. Turn on the sync in the content blocklist settings to push it into this client's Excluded file names."}
                </p>
              </div>
              {blocklistSync ? (
                <Chip tone={blocklistSync.succeeded ? "ok" : "bad"} className="shrink-0">
                  {relative(blocklistSync.syncedUtc)}
                </Chip>
              ) : null}
            </div>
          ) : null}
        </DrawerSection>
      ) : null}

      <Disclosure title="Advanced" summary={`Priority ${form.priority || "1"}`} open={advancedOpen} onOpenChange={setAdvancedOpen}>
        <Field label="Priority" error={errors.priority} help="1 (highest) to 50 (lowest). Used to order same-type clients when Deluno round-robins downloads between them.">
          <Input type="number" min={1} max={50} value={form.priority} onChange={(event) => { clearError("priority"); setForm((current) => ({ ...current, priority: event.target.value })); }} />
        </Field>
        <Field label="Tags" optional help="Leave blank to use this client for any title. With tags set, Deluno sends a release here only when its title shares one of them — for example, keep an anime library on its own client.">
          <TagPicker label="Download client tags" value={form.tags} onChange={(tags) => setForm((current) => ({ ...current, tags }))} />
        </Field>
        {preset?.kind === "Usenet" ? (
          <Field label="URL base" help={`For example "sabnzbd" if this client is reached at a path like https://host/sabnzbd/ through a reverse proxy.`}>
            <Input value={form.urlBase} onChange={(event) => setForm((current) => ({ ...current, urlBase: event.target.value }))} placeholder="sabnzbd" autoComplete="off" />
          </Field>
        ) : form.protocol === "rtorrent" ? (
          <Field label="URL base" help={`The path the webserver in front of rTorrent exposes its XML-RPC at, almost always "/RPC2".`}>
            <Input value={form.urlBase} onChange={(event) => setForm((current) => ({ ...current, urlBase: event.target.value }))} placeholder="/RPC2" autoComplete="off" />
          </Field>
        ) : null}
      </Disclosure>

      {editing ? (
        <Disclosure
          title="Advanced path mapping"
          summary={mappings.length ? `${mappings.length} saved path ${mappings.length === 1 ? "mapping" : "mappings"}` : "Only when the client and Deluno see different paths"}
          open={pathMappingOpen}
          onOpenChange={setPathMappingOpen}
        >
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            Configure completed-download folders in the client itself. Use this only when the client reports a path Deluno cannot use directly — for example, the client reports <code className="font-mono">/downloads/complete</code> while Deluno reads <code className="font-mono">D:\Downloads\complete</code>. A mapping is shared by host (#809): add it once here, and any other client at the same host reuses it automatically.
          </p>
          {mappings.length ? (
            <div className="grid gap-2">
              {mappings.map((mapping) => (
                <div key={mapping.id} className="flex min-h-10 items-center gap-2 rounded-[10px] border border-hairline px-[var(--field-pad-x)] font-mono text-[length:var(--type-caption)]">
                  <span className="min-w-0 flex-1 truncate text-muted-foreground" title={mapping.remotePath}>{mapping.remotePath}</span>
                  <span aria-hidden className="text-primary">→</span>
                  <span className="min-w-0 flex-1 truncate text-foreground" title={mapping.localPath}>{mapping.localPath}</span>
                  <Button type="button" variant="outline" size="sm" className="h-7 px-2 font-sans" onClick={() => onTestMapping(mapping.id)} disabled={busy !== null}>
                    {busy === `mapping:test:${mapping.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                    Test
                  </Button>
                  <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Remove ${mapping.remotePath} link`} onClick={() => onRemoveMapping(mapping.id)} disabled={busy !== null}>
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>
              ))}
            </div>
          ) : null}
          <FieldRow>
            <Field label={`Path reported by ${editing.name}`}>
              <Input value={newMapping.remotePath} onChange={(event) => setNewMapping({ ...newMapping, remotePath: event.target.value })} placeholder="/downloads/complete" className="font-mono text-[length:var(--type-caption)]" />
            </Field>
            <Field label="Same path as Deluno sees it">
              <Input value={newMapping.localPath} onChange={(event) => setNewMapping({ ...newMapping, localPath: event.target.value })} placeholder="D:\Downloads\complete" className="font-mono text-[length:var(--type-caption)]" />
            </Field>
          </FieldRow>
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={onAddMapping} disabled={busy !== null || !newMapping.remotePath.trim() || !newMapping.localPath.trim()}>
            {busy === "mapping:add" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />}
            Link paths
          </Button>
        </Disclosure>
      ) : null}

      {editing ? <SharingRulesEditor /> : null}

      {editing && chip ? (
        <DrawerSection title="Health">
          <dl className="grid grid-cols-[120px_1fr] items-center gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
            <dt className="text-muted-foreground">Last test</dt>
            <dd className="flex items-center gap-2"><Chip tone={chip.tone}>{chip.label}</Chip><span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span></dd>
            {editing.lastHealthMessage ? (<><dt className="text-muted-foreground">Message</dt><dd className="text-foreground">{editing.lastHealthMessage}</dd></>) : null}
            {editing.lastHealthFailure ? (
              <>
                <dt className="text-muted-foreground">Cause</dt>
                <dd className="text-foreground">
                  <span>{editing.lastHealthFailure.summary}</span>
                  <span className="mt-1 block text-muted-foreground">{editing.lastHealthFailure.nextAction}</span>
                  {editing.lastHealthFailure.retryAfterUtc ? <span className="mt-1 block text-muted-foreground">Next eligible attempt: {relative(editing.lastHealthFailure.retryAfterUtc)}</span> : null}
                </dd>
              </>
            ) : null}
          </dl>
        </DrawerSection>
      ) : null}

      {editing ? (
        <DrawerSection>
          <DrawerDanger title="Remove this client" description="Downloads already in the client are left alone." action={<Button type="button" variant="destructive" size="sm" onClick={onRemove} disabled={busy !== null}>Remove…</Button>} />
        </DrawerSection>
      ) : null}
    </>
  );
}

/**
 * The seeding-rule matrix (#820): exceptions to the global sharing rule,
 * scoped by category, tracker domain, or tag, each with its own Min Seeders
 * and Max Inactive Days guards. Shared across every download client — like
 * the sharing rule itself, a rule here is not about which client a download
 * landed in, so it is one list rather than one per client.
 */
function SharingRulesEditor() {
  const [open, setOpen] = useState(false);
  const [rules, setRules] = useState<SharingRuleItem[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState({ category: "", tracker: "", tags: "", minSeeders: "", maxInactiveDays: "" });

  useEffect(() => {
    if (!open || loaded) return;
    let cancelled = false;
    void fetchJson<SharingRuleItem[]>("/api/sharing-rules")
      .then((items) => { if (!cancelled) { setRules(items); setLoaded(true); } })
      .catch(() => { if (!cancelled) setError("Deluno could not load the seeding-rule matrix."); });
    return () => { cancelled = true; };
  }, [open, loaded]);

  const sorted = [...rules].sort((a, b) => a.priority - b.priority);

  async function addRule() {
    if (!draft.category.trim() && !draft.tracker.trim() && !draft.tags.trim()) {
      setError("Match this rule against a category, a tracker, or a tag.");
      return;
    }
    setBusy("add");
    setError(null);
    try {
      const nextPriority = sorted.length ? Math.max(...sorted.map((rule) => rule.priority)) + 1 : 1;
      const created = await fetchJson<SharingRuleItem>("/api/sharing-rules", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          priority: nextPriority,
          categoryPattern: draft.category.trim() || null,
          trackerPattern: draft.tracker.trim() || null,
          tagsCsv: draft.tags.trim() || null,
          minSeeders: draft.minSeeders.trim() ? Number(draft.minSeeders) : null,
          maxInactiveDays: draft.maxInactiveDays.trim() ? Number(draft.maxInactiveDays) : null,
          isEnabled: true
        })
      });
      setRules((current) => [...current, created]);
      setDraft({ category: "", tracker: "", tags: "", minSeeders: "", maxInactiveDays: "" });
    } catch {
      setError("This rule could not be saved.");
    } finally {
      setBusy(null);
    }
  }

  async function removeRule(id: string) {
    setBusy(`remove:${id}`);
    setError(null);
    try {
      await fetchJson(`/api/sharing-rules/${id}`, { method: "DELETE" });
      setRules((current) => current.filter((rule) => rule.id !== id));
    } catch {
      setError("This rule could not be removed.");
    } finally {
      setBusy(null);
    }
  }

  function describeRule(rule: SharingRuleItem): string {
    const scope = [
      rule.categoryPattern ? `category ${rule.categoryPattern}` : null,
      rule.trackerPattern ? `tracker ${rule.trackerPattern}` : null,
      rule.tagsCsv ? `tags ${rule.tagsCsv}` : null
    ].filter(Boolean).join(", ");
    const guards = [
      rule.minSeeders ? `hold below ${rule.minSeeders} seeders` : null,
      rule.maxInactiveDays ? `hold past ${rule.maxInactiveDays} inactive days` : null
    ].filter(Boolean).join(" · ");
    return [scope, guards].filter(Boolean).join(" — ") || "Matches nothing yet";
  }

  return (
    <Disclosure
      title="Seeding rules"
      summary={loaded && rules.length ? `${rules.length} ${rules.length === 1 ? "rule" : "rules"}` : "Exceptions to the sharing rule, by category, tracker, or tag"}
      open={open}
      onOpenChange={setOpen}
    >
      <p className="text-[length:var(--type-caption)] text-muted-foreground">
        Hold specific downloads to a different seed target, or refuse to reclaim them below a minimum seeder count or past a run of inactivity — evaluated in priority order, before Deluno&apos;s normal sharing rule.
      </p>
      {error ? <p className="text-[length:var(--type-caption)] text-destructive">{error}</p> : null}
      {sorted.length ? (
        <div className="grid gap-2">
          {sorted.map((rule) => (
            <div key={rule.id} className="flex min-h-10 items-center gap-2 rounded-[10px] border border-hairline px-[var(--field-pad-x)] text-[length:var(--type-caption)]">
              <span className="text-muted-foreground">{rule.priority}.</span>
              <span className="min-w-0 flex-1 truncate text-foreground" title={describeRule(rule)}>{describeRule(rule)}</span>
              <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Remove seeding rule ${rule.priority}`} onClick={() => removeRule(rule.id)} disabled={busy !== null}>
                {busy === `remove:${rule.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Trash2 className="h-3.5 w-3.5" />}
              </Button>
            </div>
          ))}
        </div>
      ) : null}
      <FieldRow>
        <Field label="Category" optional>
          <Input value={draft.category} onChange={(event) => setDraft((current) => ({ ...current, category: event.target.value }))} placeholder="tv-anime" autoComplete="off" />
        </Field>
        <Field label="Tracker domain" optional help="example.com or *.example.com for its subdomains">
          <Input value={draft.tracker} onChange={(event) => setDraft((current) => ({ ...current, tracker: event.target.value }))} placeholder="*.example.com" autoComplete="off" />
        </Field>
      </FieldRow>
      <FieldRow>
        <Field label="Tags" optional>
          <TagPicker label="Seeding rule tags" value={draft.tags} onChange={(tags) => setDraft((current) => ({ ...current, tags }))} />
        </Field>
      </FieldRow>
      <FieldRow>
        <Field label="Min seeders" optional help="Hold this download rather than reclaim it while fewer than this many peers are seeding it">
          <Input type="number" min={0} value={draft.minSeeders} onChange={(event) => setDraft((current) => ({ ...current, minSeeders: event.target.value }))} placeholder="e.g. 5" />
        </Field>
        <Field label="Max inactive days" optional help="Hold this download once it has gone this many days with no upload or download activity">
          <Input type="number" min={0} value={draft.maxInactiveDays} onChange={(event) => setDraft((current) => ({ ...current, maxInactiveDays: event.target.value }))} placeholder="e.g. 10" />
        </Field>
      </FieldRow>
      <Button type="button" variant="outline" size="sm" className="w-max" onClick={addRule} disabled={busy !== null}>
        {busy === "add" ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />}
        Add rule
      </Button>
    </Disclosure>
  );
}
