import { Loader2, RefreshCw, Wifi } from "lucide-react";
import type { IndexerItem, OutboundThrottleHostState } from "../../lib/api";
import { Button } from "../../components/ui/button";
import { Chip } from "../../components/ui/chip";
import { Disclosure } from "../../components/ui/disclosure";
import { DrawerDanger, DrawerSection } from "../../components/ui/drawer";
import { Field, FieldRow } from "../../components/ui/field";
import { Input } from "../../components/ui/input";
import { PresetField } from "../../components/ui/preset-field";
import { SegmentedControl } from "../../components/ui/segmented-control";
import { Select } from "../../components/ui/select";
import { SwitchRow } from "../../components/ui/switch";
import { INDEXER_PRESETS, PRIORITY_OPTIONS, type IndexerProtocol, type MediaScope } from "./presets";
import type { IndexerForm } from "./forms";
import { formatSeconds, healthChip, relative } from "./format";
export function IndexerDrawerBody({
  form,
  setForm,
  editing,
  throttle,
  errors,
  clearError,
  showKey,
  setShowKey,
  fineTuneOpen,
  setFineTuneOpen,
  busy,
  onTest,
  onReset,
  onRemove
}: {
  form: IndexerForm;
  setForm: (updater: (current: IndexerForm) => IndexerForm) => void;
  editing: IndexerItem | null;
  throttle: OutboundThrottleHostState | null;
  errors: Record<string, string>;
  clearError: (key: string) => void;
  showKey: boolean;
  setShowKey: (value: boolean) => void;
  fineTuneOpen: boolean;
  setFineTuneOpen: (value: boolean) => void;
  busy: string | null;
  onTest: () => void;
  onReset: () => void;
  onRemove: () => void;
}) {
  const preset = INDEXER_PRESETS.find((item) => item.protocol === form.protocol)!;
  const chip = editing ? healthChip(editing) : null;

  function chooseProtocol(protocol: IndexerProtocol) {
    const next = INDEXER_PRESETS.find((item) => item.protocol === protocol)!;
    setForm((current) => ({ ...current, protocol, categories: next.defaultCategories(current.scope) }));
  }
  function chooseScope(scope: MediaScope) {
    setForm((current) => ({ ...current, scope, categories: preset.defaultCategories(scope) || current.categories }));
  }

  return (
    <>
      <DrawerSection title="Basics">
        <FieldRow>
          <Field label="Name" error={errors.name}>
            <Input value={form.name} onChange={(event) => { clearError("name"); setForm((current) => ({ ...current, name: event.target.value })); }} placeholder="e.g. NZBgeek" autoComplete="off" />
          </Field>
          <Field label="Protocol" help={editing ? undefined : preset.hint}>
            <Select value={form.protocol} disabled={Boolean(editing)} onChange={(event) => chooseProtocol(event.target.value as IndexerProtocol)} options={INDEXER_PRESETS.map((item) => ({ value: item.protocol, label: item.label }))} />
          </Field>
        </FieldRow>
        <Field label={form.protocol === "torznab" ? "Torznab URL" : form.protocol === "newznab" ? "Newznab URL" : "URL"} error={errors.baseUrl}>
          <Input value={form.baseUrl} onChange={(event) => { clearError("baseUrl"); setForm((current) => ({ ...current, baseUrl: event.target.value })); }} placeholder={preset.placeholder} className="font-mono text-[length:var(--type-caption)]" autoComplete="off" spellCheck={false} />
        </Field>
        {preset.requiresApiKey || form.apiKey ? (
          <Field label="API key" error={errors.apiKey} help={editing ? "Stored encrypted. Leave blank to keep the current key; paste a new one to rotate it." : "From your indexer's account or settings page."}>
            <span className="relative block">
              <Input type={showKey ? "text" : "password"} value={form.apiKey} onChange={(event) => { clearError("apiKey"); setForm((current) => ({ ...current, apiKey: event.target.value })); }} placeholder={editing ? "••••••••••••  (unchanged)" : "Paste your API key"} className="pr-16 font-mono" autoComplete="off" spellCheck={false} />
              <button type="button" onClick={() => setShowKey(!showKey)} className="absolute right-3 top-1/2 -translate-y-1/2 text-[length:var(--type-caption)] font-medium text-primary hover:underline">
                {showKey ? "Hide" : "Reveal"}
              </button>
            </span>
          </Field>
        ) : null}
        <FieldRow>
          <Field label="Used for">
            <SegmentedControl<MediaScope> value={form.scope} onValueChange={chooseScope} options={[{ value: "both", label: "Both" }, { value: "movies", label: "Movies" }, { value: "tv", label: "TV" }]} />
          </Field>
          <Field label="Priority" help="Lower numbers are tried first.">
            <PresetField inputType="number" value={form.priority} onChange={(value) => setForm((current) => ({ ...current, priority: value }))} options={PRIORITY_OPTIONS} customLabel="Custom priority" customPlaceholder="1–50" />
          </Field>
        </FieldRow>
        <SwitchRow label="Enabled" description="Included in automatic and interactive searches." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
      </DrawerSection>

      <DrawerSection title="Categories" aside={form.categories ? `${form.categories.split(",").filter(Boolean).length} selected` : undefined}>
        <Field label="Category ids" help="Filled from the protocol and media type. Change only if your indexer uses non-standard ids.">
          <Input value={form.categories} onChange={(event) => setForm((current) => ({ ...current, categories: event.target.value }))} className="font-mono text-[length:var(--type-caption)]" placeholder="2000,2040,5000,5040" />
        </Field>
        <Disclosure title="Fine-tune" summary="Use Deluno's safe 2-second default, or follow this indexer's published limit." open={fineTuneOpen} onOpenChange={setFineTuneOpen}>
          <Field label="Request interval" error={errors.requestIntervalSeconds} help="Deluno will not query this indexer more often than this. Private trackers usually publish a limit; leave this alone if you are not sure.">
            <div className="flex items-center gap-2">
              <Input type="number" min="2" max="60" step="1" value={form.requestIntervalSeconds} onChange={(event) => { clearError("requestIntervalSeconds"); setForm((current) => ({ ...current, requestIntervalSeconds: event.target.value })); }} placeholder="Deluno default (2 seconds)" />
              <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">seconds</span>
            </div>
          </Field>
          <p className="text-[length:var(--type-caption)] text-muted-foreground">Custom intervals must be between 2 and 60 seconds. The default is 2 seconds.</p>
        </Disclosure>
      </DrawerSection>

      {editing && chip ? (
        <DrawerSection title="Health">
          <dl className="grid grid-cols-[120px_1fr] items-center gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
            <dt className="text-muted-foreground">Last test</dt>
            <dd className="flex items-center gap-2"><Chip tone={chip.tone}>{chip.label}</Chip><span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span></dd>
            <dt className="text-muted-foreground">Pacing</dt>
            <dd aria-live="polite" className="text-foreground">{throttle?.waiting ? `Deluno is waiting on ${throttle.host} before sending ${throttle.waiting} request${throttle.waiting === 1 ? "" : "s"}.` : throttle?.nextPermitInSeconds ? `Deluno will send the next request to ${throttle.host} in about ${formatSeconds(throttle.nextPermitInSeconds)}.` : "No request is waiting. Deluno will still follow this indexer's safe request interval."}</dd>
            {editing.lastHealthMessage ? (<><dt className="text-muted-foreground">Message</dt><dd className="text-foreground">{editing.lastHealthMessage}</dd></>) : null}
            {editing.consecutiveFailures > 0 ? (<><dt className="text-muted-foreground">Failures</dt><dd className="text-warning">{editing.consecutiveFailures} in a row{editing.disabledReason ? ` — ${editing.disabledReason}` : ""}</dd></>) : null}
          </dl>
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" size="sm" onClick={onTest} disabled={busy !== null || !editing.isEnabled}>
              {busy === `test:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wifi className="h-3.5 w-3.5" />}
              Test connection
            </Button>
            {editing.consecutiveFailures > 0 ? (
              <Button type="button" variant="outline" size="sm" onClick={onReset} disabled={busy !== null}>
                {busy === `reset:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
                Reset failures
              </Button>
            ) : null}
          </div>
        </DrawerSection>
      ) : null}

      {editing ? (
        <DrawerSection>
          <DrawerDanger title="Remove this indexer" description="Libraries routed only to it will need another indexer." action={<Button type="button" variant="destructive" size="sm" onClick={onRemove} disabled={busy !== null}>Remove…</Button>} />
        </DrawerSection>
      ) : null}
    </>
  );
}
