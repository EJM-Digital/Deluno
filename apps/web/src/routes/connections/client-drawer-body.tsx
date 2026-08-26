import { useEffect, useState } from "react";
import { Loader2, Plus, Trash2, Wifi } from "lucide-react";
import type { DownloadClientItem, DownloadClientPathMappingItem } from "../../lib/api";
import { Button } from "../../components/ui/button";
import { Chip } from "../../components/ui/chip";
import { Disclosure } from "../../components/ui/disclosure";
import { DrawerDanger, DrawerSection } from "../../components/ui/drawer";
import { Field, FieldRow } from "../../components/ui/field";
import { Input } from "../../components/ui/input";
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
  onTest,
  onRemove
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
  onTest: () => void;
  onRemove: () => void;
}) {
  const [pathMappingOpen, setPathMappingOpen] = useState(false);
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
      name: current.name.trim() && !CLIENT_PRESETS.some((item) => item.label === current.name) ? current.name : next.label
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
        <FieldRow>
          <Field label="Username" optional>
            <Input value={form.username} onChange={(event) => setForm((current) => ({ ...current, username: event.target.value }))} autoComplete="off" />
          </Field>
          <Field label={preset?.authMode === "API key" ? "API key" : "Password"} optional help={editing ? "Stored encrypted. Leave empty to keep the current one." : preset?.authMode === "API key" ? "From the client's settings page." : undefined}>
            <Input type="password" value={form.password} onChange={(event) => setForm((current) => ({ ...current, password: event.target.value }))} placeholder={editing ? "••••••••  (unchanged)" : ""} autoComplete="new-password" />
          </Field>
        </FieldRow>
        <SwitchRow label="Enabled" description="Deluno may send approved releases to this client." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
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

      {editing ? (
        <Disclosure
          title="Advanced path mapping"
          summary={mappings.length ? `${mappings.length} saved path ${mappings.length === 1 ? "mapping" : "mappings"}` : "Only when the client and Deluno see different paths"}
          open={pathMappingOpen}
          onOpenChange={setPathMappingOpen}
        >
          <p className="text-[length:var(--type-caption)] text-muted-foreground">
            Configure completed-download folders in the client itself. Use this only when the client reports a path Deluno cannot use directly — for example, the client reports <code className="font-mono">/downloads/complete</code> while Deluno reads <code className="font-mono">D:\Downloads\complete</code>.
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

      {editing && chip ? (
        <DrawerSection title="Health">
          <dl className="grid grid-cols-[120px_1fr] items-center gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
            <dt className="text-muted-foreground">Last test</dt>
            <dd className="flex items-center gap-2"><Chip tone={chip.tone}>{chip.label}</Chip><span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span></dd>
            {editing.lastHealthMessage ? (<><dt className="text-muted-foreground">Message</dt><dd className="text-foreground">{editing.lastHealthMessage}</dd></>) : null}
          </dl>
          <Button type="button" variant="outline" size="sm" className="w-max" onClick={onTest} disabled={busy !== null}>
            {busy === `test:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
            Test connection
          </Button>
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
