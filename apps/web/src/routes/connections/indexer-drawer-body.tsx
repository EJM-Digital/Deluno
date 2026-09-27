import { useState } from "react";
import { Loader2, RefreshCw } from "lucide-react";
import type { IndexerItem, OutboundThrottleHostState, PlatformSettingsSnapshot, ProxyItem } from "../../lib/api";
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
import type { IndexerForm, SharingRule } from "./forms";
import { describeCustomSharingRule, describeGlobalSharingRule, describeSeasonPackSeedTime, describeStrictSharingRule } from "../../lib/sharing-rule";
import { STRICT_SHARING } from "./forms";
import { formatSeconds, healthChip, relative, vipWarningActive } from "./format";
import { CategoryPicker } from "./category-picker";
import { IndexerFlagPicker } from "./indexer-flag-picker";
import { TrackerFields } from "./tracker-fields";
export function IndexerDrawerBody({
  form,
  setForm,
  editing,
  settings,
  proxies,
  throttle,
  errors,
  clearError,
  showKey,
  setShowKey,
  fineTuneOpen,
  setFineTuneOpen,
  busy,
  onReset,
  onAcknowledgeVip,
  onRemove
}: {
  form: IndexerForm;
  setForm: (updater: (current: IndexerForm) => IndexerForm) => void;
  editing: IndexerItem | null;
  /** Only for stating the user's own sharing rule back to them (#288). */
  settings: PlatformSettingsSnapshot;
  /** Configured indexer proxies (#748) this indexer can be assigned to. */
  proxies: ProxyItem[];
  throttle: OutboundThrottleHostState | null;
  errors: Record<string, string>;
  clearError: (key: string) => void;
  showKey: boolean;
  setShowKey: (value: boolean) => void;
  fineTuneOpen: boolean;
  setFineTuneOpen: (value: boolean) => void;
  busy: string | null;
  onReset: () => void;
  onAcknowledgeVip: () => void;
  onRemove: () => void;
}) {
  const preset = INDEXER_PRESETS.find((item) => item.protocol === form.protocol)!;
  const chip = editing ? healthChip(editing) : null;
  // Category ids are Newznab/Torznab numbers (2000 movies, 5000 TV...). Deluno
  // fills them from the protocol and what the indexer is used for, so they stay
  // folded away and are described in words; only an indexer with non-standard
  // numbers needs them opened.
  const [categoriesOpen, setCategoriesOpen] = useState(false);
  const standardCategories = sameCategories(form.categories, preset.defaultCategories(form.scope));
  const scopeWords = form.scope === "movies" ? "Movies" : form.scope === "tv" ? "TV" : "Movies and TV";
  const categoryCount = form.categories.split(",").map((item) => item.trim()).filter(Boolean).length;
  const categoriesSummary = standardCategories
    ? `The standard ${scopeWords} categories for ${preset.label}.`
    : `Custom: ${categoryCount} ${categoryCount === 1 ? "category" : "categories"} chosen for this indexer.`;

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
        {form.protocol === "tracker" ? (
          <TrackerFields form={form} setForm={setForm} editing={editing} errors={errors} clearError={clearError} />
        ) : (
        <>
        <Field label={form.protocol === "torznab" ? "Torznab URL" : form.protocol === "newznab" ? "Newznab URL" : "URL"} error={errors.baseUrl}>
          <Input value={form.baseUrl} onChange={(event) => { clearError("baseUrl"); setForm((current) => ({ ...current, baseUrl: event.target.value })); }} placeholder={preset.placeholder} className="font-mono text-[length:var(--type-caption)]" autoComplete="off" spellCheck={false} />
        </Field>
        {form.protocol === "newznab" || form.protocol === "torznab" ? (
          <Field label="API path" help="Only needed if this indexer's API does not live at the default /api.">
            <Input value={form.apiPath} onChange={(event) => setForm((current) => ({ ...current, apiPath: event.target.value }))} placeholder="/api" className="font-mono text-[length:var(--type-caption)]" autoComplete="off" spellCheck={false} />
          </Field>
        ) : null}
        {preset.requiresApiKey || form.apiKey ? (
          <Field label="API key" error={errors.apiKey} help={editing ? "Stored encrypted. Leave empty to keep the current key; paste a new one to rotate it." : "From your indexer's account or settings page."}>
            <span className="relative block">
              <Input type={showKey ? "text" : "password"} value={form.apiKey} onChange={(event) => { clearError("apiKey"); setForm((current) => ({ ...current, apiKey: event.target.value })); }} placeholder={editing ? "••••••••••••  (unchanged)" : "Paste your API key"} className="pr-16 font-mono" autoComplete="off" spellCheck={false} />
              <button type="button" onClick={() => setShowKey(!showKey)} className="absolute right-3 top-1/2 -translate-y-1/2 text-[length:var(--type-caption)] font-medium text-primary hover:underline">
                {showKey ? "Hide" : "Reveal"}
              </button>
            </span>
          </Field>
        ) : null}
        </>
        )}
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

      {/* Asked here, while the user is looking at the site's own rules, rather
          than left for them to discover after an account is banned (#288). One
          question: is this site stricter than the rule you already made? The
          five settings behind the answer are Deluno's problem, not theirs. */}
      <DrawerSection title="Sharing">
        <Field
          label="Does this site expect you to keep sharing?"
          help="Most public sites do not care. Private trackers usually do, and can penalise an account that stops early."
        >
          <SegmentedControl<SharingRule>
            value={form.sharingRule}
            onValueChange={(sharingRule) => setForm((current) => ({
              ...current,
              sharingRule,
              // The strict preset's own numbers, so switching to "custom" from
              // "strict" starts from what the site was already being held to
              // rather than blank fields (#739).
              sharingSeedHours: sharingRule === "custom" && !current.sharingSeedHours ? String(STRICT_SHARING.forHours) : current.sharingSeedHours,
              sharingSeedRatio: sharingRule === "custom" && !current.sharingSeedRatio ? String(STRICT_SHARING.untilRatio) : current.sharingSeedRatio
            }))}
            options={[
              { value: "inherit", label: "No, my normal rule" },
              { value: "strict", label: "Yes, it is strict" },
              { value: "custom", label: "Set my own numbers" }
            ]}
          />
        </Field>
        {form.sharingRule === "custom" ? (
          <FieldRow>
            <Field label="Seed time" help="This tracker's published minimum seed time.">
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step="1" value={form.sharingSeedHours} onChange={(event) => setForm((current) => ({ ...current, sharingSeedHours: event.target.value }))} placeholder="e.g. 336" />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">hours</span>
              </div>
            </Field>
            <Field label="Seed ratio" help="This tracker's published minimum ratio.">
              <Input type="number" min="0" step="0.05" value={form.sharingSeedRatio} onChange={(event) => setForm((current) => ({ ...current, sharingSeedRatio: event.target.value }))} placeholder="e.g. 1.0" />
            </Field>
          </FieldRow>
        ) : null}
        <p className="text-[length:var(--type-caption)] text-muted-foreground">
          {form.sharingRule === "strict"
            ? describeStrictSharingRule()
            : form.sharingRule === "custom"
              ? describeCustomSharingRule(form.sharingSeedHours.trim() ? Number(form.sharingSeedHours) : null, form.sharingSeedRatio.trim() ? Number(form.sharingSeedRatio) : null)
              : describeGlobalSharingRule(settings)}
        </p>
        {form.sharingRule !== "inherit" ? (
          <>
            <Field label="Season-pack seed time" help="Only for whole-season downloads from this source. Leave blank to use the seed time above for season packs too.">
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step="1" value={form.seasonPackSeedForHours} onChange={(event) => setForm((current) => ({ ...current, seasonPackSeedForHours: event.target.value }))} placeholder="Same as normal" />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">hours</span>
              </div>
            </Field>
            <p className="text-[length:var(--type-caption)] text-muted-foreground">
              {describeSeasonPackSeedTime(
                form.seasonPackSeedForHours.trim() ? Number(form.seasonPackSeedForHours) : null,
                form.sharingRule === "strict" ? STRICT_SHARING.forHours : (form.sharingSeedHours.trim() ? Number(form.sharingSeedHours) : null)
              )}
            </p>
          </>
        ) : null}
      </DrawerSection>

      <DrawerSection title="Search details">
        <Disclosure title="Categories" summary={categoriesSummary} open={categoriesOpen} onOpenChange={setCategoriesOpen}>
          <CategoryPicker value={form.categories} scope={form.scope} onChange={(categories) => setForm((current) => ({ ...current, categories }))} />
          {standardCategories ? null : (
            <Button type="button" variant="outline" size="sm" onClick={() => setForm((current) => ({ ...current, categories: preset.defaultCategories(current.scope) }))}>
              Use the standard {scopeWords} categories
            </Button>
          )}
          <Field label="Category ids" help="A number your indexer uses that the picker above doesn't know a name for yet. Comma-separated; ticking a box above edits this too.">
            <Input value={form.categories} onChange={(event) => setForm((current) => ({ ...current, categories: event.target.value }))} className="font-mono text-[length:var(--type-caption)]" placeholder="2000,2040,5000,5040" />
          </Field>
          {form.scope === "tv" || form.scope === "both" ? (
            <Field label="Anime categories" help="This indexer's own category numbers for anime, if it catalogues anime separately from normal TV. Leave blank to use the categories above for anime too.">
              <Input value={form.animeCategories} onChange={(event) => setForm((current) => ({ ...current, animeCategories: event.target.value }))} className="font-mono text-[length:var(--type-caption)]" placeholder="5070" />
            </Field>
          ) : null}
          {form.scope === "tv" || form.scope === "both" ? (
            <Field label="TV search scope" help="Restrict TV searches against this indexer to season packs, single episodes, or both.">
              <SegmentedControl<"both" | "season-packs-only" | "episodes-only">
                value={form.seasonPackSearchMode}
                onValueChange={(seasonPackSearchMode) => setForm((current) => ({ ...current, seasonPackSearchMode }))}
                options={[
                  { value: "both", label: "Both" },
                  { value: "season-packs-only", label: "Season packs only" },
                  { value: "episodes-only", label: "Episodes only" }
                ]}
              />
            </Field>
          ) : null}
        </Disclosure>
        <Disclosure title="Fine-tune" summary="Use Deluno's safe 2-second default, or follow this indexer's published limit." open={fineTuneOpen} onOpenChange={setFineTuneOpen}>
          <Field label="Request interval" error={errors.requestIntervalSeconds} help="Deluno will not query this indexer more often than this. Private trackers usually publish a limit; leave this alone if you are not sure.">
            <div className="flex items-center gap-2">
              <Input type="number" min="2" max="60" step="1" value={form.requestIntervalSeconds} onChange={(event) => { clearError("requestIntervalSeconds"); setForm((current) => ({ ...current, requestIntervalSeconds: event.target.value })); }} placeholder="Deluno default (2 seconds)" />
              <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">seconds</span>
            </div>
          </Field>
          <p className="text-[length:var(--type-caption)] text-muted-foreground">Custom intervals must be between 2 and 60 seconds. The default is 2 seconds.</p>
          <FieldRow>
            <Field label="Query limit" error={errors.queryLimit} help="A cap on searches sent to this indexer, in addition to the request interval above.">
              <Input type="number" min="0" step="1" value={form.queryLimit} onChange={(event) => { clearError("queryLimit"); setForm((current) => ({ ...current, queryLimit: event.target.value })); }} placeholder="No cap" />
            </Field>
            <Field label="Grab limit" error={errors.grabLimit} help="A cap on grabs sent to this indexer.">
              <Input type="number" min="0" step="1" value={form.grabLimit} onChange={(event) => { clearError("grabLimit"); setForm((current) => ({ ...current, grabLimit: event.target.value })); }} placeholder="No cap" />
            </Field>
          </FieldRow>
          {form.queryLimit.trim() || form.grabLimit.trim() ? (
            <Field label="Limits unit" help="Whether the query and grab limits above count per day or per hour.">
              <SegmentedControl<"day" | "hour">
                value={form.limitsUnit}
                onValueChange={(limitsUnit) => setForm((current) => ({ ...current, limitsUnit }))}
                options={[{ value: "day", label: "Per day" }, { value: "hour", label: "Per hour" }]}
              />
            </Field>
          ) : null}
          {form.protocol === "newznab" || form.protocol === "torznab" ? (
            <Field label="Additional parameters" help="Extra query parameters some indexers require, such as a site-specific token or flag.">
              <Input value={form.additionalParameters} onChange={(event) => setForm((current) => ({ ...current, additionalParameters: event.target.value }))} className="font-mono text-[length:var(--type-caption)]" placeholder="token=abc123" autoComplete="off" spellCheck={false} />
            </Field>
          ) : null}
          {form.scope === "tv" || form.scope === "both" ? (
            <Field label="What MULTI means here" error={errors.multiLanguages} help="This indexer's own languages for a release tagged MULTI, e.g. Original, French. Leave blank to use Deluno's default of 'Multilingual'.">
              <Input value={form.multiLanguages} onChange={(event) => { clearError("multiLanguages"); setForm((current) => ({ ...current, multiLanguages: event.target.value })); }} placeholder="Original, French" />
            </Field>
          ) : null}
          <Field label="Proxy" help="Send this indexer's requests through one of your configured proxies instead of connecting directly.">
            <Select
              value={form.proxyId}
              onChange={(event) => setForm((current) => ({ ...current, proxyId: event.target.value }))}
              options={[{ value: "", label: "Direct connection (no proxy)" }, ...proxies.map((proxy) => ({ value: proxy.id, label: proxy.name }))]}
            />
          </Field>
        </Disclosure>
      </DrawerSection>

      <DrawerSection title="Acquisition policy">
        <p className="text-[length:var(--type-caption)] text-muted-foreground">
          These rules decide which feed results Deluno can use. An empty limit keeps the indexer unrestricted.
        </p>
        <Disclosure title="Timing, limits, and flags" summary="Applied before a release can be sent to a download client." open={fineTuneOpen} onOpenChange={setFineTuneOpen}>
          <FieldRow>
            <Field label="Minimum age" error={errors.minimumAgeMinutes} help="Wait for a Usenet or torrent post to propagate before grabbing it.">
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step="1" value={form.minimumAgeMinutes} onChange={(event) => { clearError("minimumAgeMinutes"); setForm((current) => ({ ...current, minimumAgeMinutes: event.target.value })); }} placeholder="No minimum" />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">min</span>
              </div>
            </Field>
            <Field label="Retention" error={errors.retentionDays} help="Reject releases older than the number of days your provider keeps.">
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step="1" value={form.retentionDays} onChange={(event) => { clearError("retentionDays"); setForm((current) => ({ ...current, retentionDays: event.target.value })); }} placeholder="No limit" />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">days</span>
              </div>
            </Field>
          </FieldRow>
          <FieldRow>
            <Field label="Maximum size" error={errors.maximumSizeMb} help="Reject releases larger than this, in megabytes.">
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step="1" value={form.maximumSizeMb} onChange={(event) => { clearError("maximumSizeMb"); setForm((current) => ({ ...current, maximumSizeMb: event.target.value })); }} placeholder="No limit" />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">MB</span>
              </div>
            </Field>
            <Field label="Availability delay" error={errors.availabilityDelayDays} help="Hold a title for this many days after its known availability date.">
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step="1" value={form.availabilityDelayDays} onChange={(event) => { clearError("availabilityDelayDays"); setForm((current) => ({ ...current, availabilityDelayDays: event.target.value })); }} placeholder="No delay" />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">days</span>
              </div>
            </Field>
          </FieldRow>
          {form.protocol !== "newznab" ? (
            <Field label="Minimum seeders" error={errors.minimumSeeders} help="Reject a torrent with fewer seeders than this before it can be grabbed.">
              <div className="flex items-center gap-2">
                <Input type="number" min="0" step="1" value={form.minimumSeeders} onChange={(event) => { clearError("minimumSeeders"); setForm((current) => ({ ...current, minimumSeeders: event.target.value })); }} placeholder="No minimum" />
                <span className="shrink-0 text-[length:var(--type-body-sm)] text-muted-foreground">seeders</span>
              </div>
            </Field>
          ) : null}
          <Field label="Preferred indexer flags" error={errors.preferIndexerFlags} help="Pick a flag this indexer reports, or type your own below; matching releases receive a ranking boost.">
            <IndexerFlagPicker
              value={form.preferIndexerFlags}
              onChange={(next) => { clearError("preferIndexerFlags"); setForm((current) => ({ ...current, preferIndexerFlags: next })); }}
            />
            <Input className="mt-1.5" value={form.preferIndexerFlags} onChange={(event) => { clearError("preferIndexerFlags"); setForm((current) => ({ ...current, preferIndexerFlags: event.target.value })); }} placeholder="freeleech, double-upload" />
          </Field>
          <FieldRow>
            <Field label="Required words" error={errors.requiredWords} help="Reject a release from this indexer unless its title contains every word here.">
              <Input value={form.requiredWords} onChange={(event) => { clearError("requiredWords"); setForm((current) => ({ ...current, requiredWords: event.target.value })); }} placeholder="e.g. remux, bluray" />
            </Field>
            <Field label="Ignored words" error={errors.ignoredWords} help="Reject a release from this indexer if its title contains any word here.">
              <Input value={form.ignoredWords} onChange={(event) => { clearError("ignoredWords"); setForm((current) => ({ ...current, ignoredWords: event.target.value })); }} placeholder="e.g. cam, korsub" />
            </Field>
          </FieldRow>
          <p className="text-[length:var(--type-caption)] text-muted-foreground">The timing rules hold or reject a candidate; they do not alter the indexer's results.</p>
        </Disclosure>
        <div className="flex flex-col gap-2">
          {form.protocol !== "newznab" ? (
            <SwitchRow label="Prefer magnet links" description="When a grab has both a magnet link and a .torrent file, send the magnet link to the download client." checked={form.preferMagnetUrl} onCheckedChange={(checked) => setForm((current) => ({ ...current, preferMagnetUrl: checked }))} />
          ) : null}
          <SwitchRow label="RSS feed" description="Include this indexer's RSS results when RSS is the source." checked={form.rssEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, rssEnabled: checked }))} />
          <SwitchRow label="Automatic search" description="Allow scheduled library searches to query this indexer." checked={form.automaticSearchEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, automaticSearchEnabled: checked }))} />
          <SwitchRow label="Interactive search" description="Allow searches started from a title or episode page to query this indexer." checked={form.interactiveSearchEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, interactiveSearchEnabled: checked }))} />
        </div>
      </DrawerSection>

      {editing && chip ? (
        <DrawerSection title="Health">
          <Field label="VIP/subscription expiry" error={errors.vipExpiresUtc} help="For a private tracker with a VIP or membership tier. Deluno warns starting 7 days before this date and until you update it or acknowledge the warning.">
            <Input type="date" value={form.vipExpiresUtc} onChange={(event) => { clearError("vipExpiresUtc"); setForm((current) => ({ ...current, vipExpiresUtc: event.target.value })); }} />
          </Field>
          {vipWarningActive(editing) ? (
            <div className="flex items-center gap-2">
              <Chip tone={new Date(editing.vipExpiresUtc!).getTime() <= Date.now() ? "bad" : "warn"}>
                {new Date(editing.vipExpiresUtc!).getTime() <= Date.now() ? "VIP expired" : "VIP expiring soon"}
              </Chip>
              <Button type="button" variant="outline" size="sm" onClick={onAcknowledgeVip} disabled={busy !== null}>
                {busy === `acknowledge-vip:${editing.id}` ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                Acknowledge
              </Button>
            </div>
          ) : null}
          <dl className="grid grid-cols-[120px_1fr] items-center gap-x-[var(--grid-gap)] gap-y-2 text-[length:var(--type-body-sm)]">
            <dt className="text-muted-foreground">Last test</dt>
            <dd className="flex items-center gap-2"><Chip tone={chip.tone}>{chip.label}</Chip><span className="text-muted-foreground">{relative(editing.lastHealthTestUtc)}{editing.lastHealthLatencyMs != null ? ` · ${editing.lastHealthLatencyMs} ms` : ""}</span></dd>
            <dt className="text-muted-foreground">Pacing</dt>
            <dd aria-live="polite" className="text-foreground">{throttle?.waiting ? `Deluno is waiting on ${throttle.host} before sending ${throttle.waiting} request${throttle.waiting === 1 ? "" : "s"}.` : throttle?.nextPermitInSeconds ? `Deluno will send the next request to ${throttle.host} in about ${formatSeconds(throttle.nextPermitInSeconds)}.` : "No request is waiting. Deluno will still follow this indexer's safe request interval."}</dd>
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
            {editing.consecutiveFailures > 0 ? (<><dt className="text-muted-foreground">Failures</dt><dd className="text-warning">{editing.consecutiveFailures} in a row{editing.disabledReason ? ` — ${editing.disabledReason}` : ""}</dd></>) : null}
          </dl>
          <div className="flex flex-wrap gap-2">
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

/** The same category ids, whatever the order or spacing. */
function sameCategories(left: string, right: string): boolean {
  const ids = (value: string) => value.split(",").map((item) => item.trim()).filter(Boolean).sort().join(",");
  return ids(left) === ids(right);
}
