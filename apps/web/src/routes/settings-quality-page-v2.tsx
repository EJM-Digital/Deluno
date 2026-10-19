/**
 * Size Rules — a page-level form on the shared grammar.
 *
 *   PageToolbar (Library Profiles tabs · Movies / TV)
 *   ListCard (one media type at a time, tiers grouped into collapsible
 *             resolution bands on one square-root ruler with a drawn axis)
 *   ListCard (upgrade behaviour)
 *   PageFooter (pinned: status · Save)
 *
 * Contracts: GET/PUT /api/quality-model.
 */
import { useEffect, useMemo, useState, type FormEvent, type ReactNode } from "react";
import { useLoaderData } from "react-router-dom";
import { useFollowLoaded } from "../hooks/use-follow-loaded";
import { useLiveRefresh, type LiveRefreshSpec } from "../lib/live-refresh";
import { ConfigAreas } from "../lib/config-areas";
import { ChevronRight } from "lucide-react";
import { Button } from "../components/ui/button";
import { ConfirmDialog } from "../components/ui/confirm-dialog";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { FilterChips } from "../components/ui/filter-chips";
import { RangeAxis, RangeSlider } from "../components/ui/range-slider";
import { SwitchRow } from "../components/ui/switch";
import { configurationNavAreas } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { fetchJson, type QualityModelSnapshot, type QualityTierDefinition } from "../lib/api";
import { cn } from "../lib/utils";
import type { DrawerSaveState } from "../components/ui/drawer";
import { resolveDrawerSaveState, shouldClearSaveState } from "../lib/drawer-save-state";

const TABS = configurationNavAreas.find((area) => area.label === "Quality & Release")?.items ?? [];

interface LoaderData {
  qualityModel: QualityModelSnapshot;
}

export async function settingsQualityLoader(): Promise<LoaderData> {
  return { qualityModel: await fetchJson<QualityModelSnapshot>("/api/quality-model") };
}

/** What makes this page read its data again (#1179). */
const SETTINGS_REFRESH: LiveRefreshSpec = { events: ["QualityProfileChanged"], areas: [ConfigAreas.qualityModel] };

export function SettingsQualityPage() {
  useLiveRefresh(SETTINGS_REFRESH);
  const { qualityModel: loaded } = useLoaderData() as LoaderData;
  const [saved, setSaved] = useState(loaded);
  const [model, setModel] = useState(loaded);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);
  const [confirmingReset, setConfirmingReset] = useState(false);
  const [resetting, setResetting] = useState(false);

  // One table at a time: 26 tiers × two units is a page nobody can scan.
  const [scope, setScope] = useState<"movie" | "episode">("movie");
  const dirty = useMemo(() => JSON.stringify(model) !== JSON.stringify(saved), [model, saved]);
  const state: DrawerSaveState = resolveDrawerSaveState(saveState, dirty);
  useUnsavedChanges(dirty);
  useFollowLoaded(loaded, dirty, (next) => { setSaved(next); setModel(next); });

  useEffect(() => {
    if (shouldClearSaveState(saveState, dirty)) setSaveState(undefined);
  }, [dirty, saveState]);

  function updateTier(index: number, key: keyof QualityTierDefinition, value: number) {
    setModel((current) => ({
      ...current,
      tiers: current.tiers.map((tier, tierIndex) => (tierIndex === index ? { ...tier, [key]: Number.isFinite(value) ? value : 0 } : tier))
    }));
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (state === "saving") return;
    setSaveState("saving");
    try {
      const next = await fetchJson<QualityModelSnapshot>("/api/quality-model", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tiers: model.tiers, upgradeStop: model.upgradeStop })
      });
      setSaved(next);
      setModel(next);
      setSaveState("saved");
      setMessage("Saved just now");
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not save size rules");
    }
  }

  async function handleReset() {
    setResetting(true);
    try {
      const next = await fetchJson<QualityModelSnapshot>("/api/quality-model/reset", { method: "POST" });
      setSaved(next);
      setModel(next);
      setSaveState("saved");
      setMessage("Reset to Deluno's shipped defaults");
    } catch (error) {
      setSaveState("error");
      setMessage(error instanceof Error ? error.message : "Could not reset size rules");
    } finally {
      setResetting(false);
      setConfirmingReset(false);
    }
  }

  const scopeSwitch = (
    <FilterChips<"movie" | "episode">
      label="Show sizes for"
      size="sm"
      value={scope}
      onValueChange={setScope}
      options={[
        { value: "movie", label: "Movies" },
        { value: "episode", label: "TV" }
      ]}
    />
  );

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-(--page-gap)" noValidate>
      <PageToolbar
        tabs={TABS}
        actions={
          <Button type="button" variant="outline" size="sm" onClick={() => setConfirmingReset(true)}>
            Reset to defaults
          </Button>
        }
      />

      {/* "Movies · TV" is the vocabulary MediaTypeFilter already uses everywhere else. */}
      {scope === "movie" ? (
        <SizeCard
          scope="movie"
          unit="GB"
          tiers={model.tiers}
          minKey="movieMinGb"
          preferredKey="moviePreferredGb"
          maxKey="movieMaxGb"
          step={0.1}
          onChange={updateTier}
          switcher={scopeSwitch}
        />
      ) : (
        <SizeCard
          scope="episode"
          unit="MB"
          tiers={model.tiers}
          minKey="episodeMinMb"
          preferredKey="episodePreferredMb"
          maxKey="episodeMaxMb"
          step={100}
          onChange={updateTier}
          switcher={scopeSwitch}
        />
      )}

      <ListCard title="Same-quality upgrades" count="How Deluno chooses between equally ranked releases">
        <div className="grid gap-(--grid-gap) p-(--card-pad-x)">
          <SwitchRow
            label="Require a preference improvement at the same quality"
            description="Only replace an equally ranked release when its typed preference outcome is better."
            checked={model.upgradeStop.requireCustomFormatGainForSameQuality}
            onCheckedChange={(checked) => setModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, requireCustomFormatGainForSameQuality: checked } }))}
          />
        </div>
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save size rules" />

      <ConfirmDialog
        open={confirmingReset}
        onOpenChange={setConfirmingReset}
        title="Reset size rules to defaults?"
        description="Every tier's Min, Preferred and Max sizes go back to Deluno's shipped numbers. Anything you've hand-edited here is lost. This is recorded in Activity."
        confirmLabel="Reset to defaults"
        busy={resetting}
        onConfirm={() => void handleReset()}
      />
    </form>
  );
}

/* ---------------------------------------------------------------- card */

/** Round a ceiling up to something readable so the shared scale ends on a tidy number. */
function niceCeiling(value: number) {
  if (value <= 0) return 1;
  const magnitude = 10 ** Math.floor(Math.log10(value));
  return Math.ceil(value / (magnitude / 2)) * (magnitude / 2);
}

/**
 * One template for the column header, the axis, the band rows and the tier rows,
 * so every one of them lines up. `pr-2` on the numeric columns matches the number
 * inputs' own padding, which is what keeps a header label over its own digits.
 */
const SIZE_GRID = "grid grid-cols-[minmax(11rem,1.3fr)_minmax(12rem,2.4fr)_5.5rem_8.5rem_5.5rem] items-center gap-(--grid-gap) px-(--card-pad-x)";

function SizeCard({
  scope,
  unit,
  tiers,
  minKey,
  preferredKey,
  maxKey,
  step,
  onChange,
  switcher
}: {
  /** Disambiguates control labels between the movie and episode tables. */
  scope: "movie" | "episode";
  unit: string;
  tiers: QualityTierDefinition[];
  minKey: keyof QualityTierDefinition;
  /** Radarr calls this column Preferred (#771): the size scoring aims for within Min/Max. */
  preferredKey: keyof QualityTierDefinition;
  maxKey: keyof QualityTierDefinition;
  step: number;
  onChange: (index: number, key: keyof QualityTierDefinition, value: number) => void;
  /** The Movies | TV switch: it chooses which table this card shows, so it sits in the card's own header. */
  switcher?: ReactNode;
}) {
  const format = (value: number) => (unit === "GB" ? `${value} ${unit}` : `${value.toLocaleString()} ${unit}`);
  // 0 as a maximum means "no upper limit", matching the backend's convention.
  const formatMax = (value: number) => (value === 0 ? "Unlimited" : format(value));
  const bands = useMemo(() => groupTiers(tiers, minKey, maxKey), [tiers, minKey, maxKey]);
  // One ruler for the whole table. It is square-root scaled and drawn as an axis,
  // because sizes run from 0.1 to 130 and a linear ruler buries everything small.
  const scaleMax = useMemo(
    () => niceCeiling(Math.max(...tiers.flatMap((tier) => [Number(tier[minKey]), Number(tier[maxKey])]), 1)),
    [tiers, minKey, maxKey]
  );
  const [open, setOpen] = useState<Record<string, boolean>>(() =>
    Object.fromEntries(bands.map((band) => [band.label, DEFAULT_OPEN_BANDS.includes(band.label)]))
  );
  const allOpen = bands.every((band) => open[band.label]);

  return (
    <ListCard
      title={scope === "movie" ? "Movie file sizes" : "Episode file sizes"}
      count={`The final check that rejects a release as implausibly small or large. 0 and 0 means no limit.`}
      actions={
        <>
          {switcher}
          <Button type="button" variant="outline" size="sm" onClick={() => setOpen(Object.fromEntries(bands.map((band) => [band.label, !allOpen])))}>
            {allOpen ? "Collapse all" : "Expand all"}
          </Button>
        </>
      }
    >
      <div className="overflow-x-auto">
        <div className="min-w-232">
          <div className={cn(SIZE_GRID, "h-(--list-thead-height) border-b border-hairline bg-surface-2/40")}>
            {["Quality tier", "Accepted range", `Min (${unit})`, `Preferred (${unit})`, `Max (${unit})`].map((label, index) => (
              <span
                key={label}
                className={cn(
                  "truncate text-(length:--type-micro) font-semibold uppercase tracking-widest text-muted-foreground",
                  index >= 2 && "pr-2 text-right"
                )}
              >
                {label}
              </span>
            ))}
          </div>

          <div className={cn(SIZE_GRID, "border-b border-hairline bg-surface-2/40 pb-2")}>
            <span />
            <RangeAxis scaleMax={scaleMax} scale="sqrt" unit={unit} />
            <span />
            <span />
            <span />
          </div>

          {bands.map((band) => {
            const expanded = Boolean(open[band.label]);
            return (
              <div key={band.label}>
                <button
                  type="button"
                  aria-expanded={expanded}
                  onClick={() => setOpen((current) => ({ ...current, [band.label]: !current[band.label] }))}
                  className={cn(
                    SIZE_GRID,
                    // Same weight as ListGroupHeader: a heading for the rows under it,
                    // not another hairline between them.
                    "sticky top-0 z-10 h-(--list-group-height) w-full border-y border-hairline bg-surface-3 text-left",
                    "transition-colors hover:bg-surface-3/70 focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
                  )}
                >
                  <span className="flex min-w-0 items-center gap-2 text-(length:--type-caption) font-semibold uppercase tracking-[0.12em] text-foreground">
                    <ChevronRight aria-hidden className={cn("h-4 w-4 shrink-0 text-muted-foreground transition-transform", expanded && "rotate-90")} />
                    <span className="truncate">{band.label}</span>
                  </span>
                  <span />
                  {/* The band summary sits in the Min and Max columns, over the numbers it summarises. Preferred is per-tier, not per-band. */}
                  <span className="pr-2 text-right text-(length:--type-caption) tabular-nums text-muted-foreground">
                    {band.unruled ? "—" : format(band.spanMin)}
                  </span>
                  <span />
                  <span className="pr-2 text-right text-(length:--type-caption) tabular-nums text-muted-foreground">
                    {band.unruled ? "—" : formatMax(band.spanMax)}
                  </span>
                </button>

                {expanded
                  ? band.tiers.map(({ tier, index }) => {
                      const low = Number(tier[minKey]);
                      const high = Number(tier[maxKey]);
                      const preferred = Number(tier[preferredKey]);
                      const unset = low === 0 && high === 0;
                      return (
                        <div key={tier.name} className={cn(SIZE_GRID, "min-h-(--list-row-height) border-b border-hairline last:border-b-0")}>
                          <div className="min-w-0">
                            <span className="block truncate text-(length:--type-body-sm) font-medium text-foreground">{tier.name}</span>
                            <span className="mt-0.5 block truncate text-(length:--type-caption) text-muted-foreground">
                              Rank {tier.rank}
                              {unset ? " · any size" : ""}
                            </span>
                          </div>
                          <RangeSlider
                            min={low}
                            max={high}
                            step={step}
                            scaleMax={scaleMax}
                            scale="sqrt"
                            zeroMaxIsUnlimited
                            formatValue={format}
                            minLabel={`${tier.name} ${scope} minimum`}
                            maxLabel={`${tier.name} ${scope} maximum`}
                            onChange={(next) => {
                              if (next.min !== low) onChange(index, minKey, round(next.min, step));
                              if (next.max !== high) onChange(index, maxKey, round(next.max, step));
                            }}
                          />
                          <SizeInput label={`${tier.name} ${scope} minimum value`} value={low} max={scaleMax} step={step} onChange={(value) => onChange(index, minKey, value)} />
                          <SizeInput label={`${tier.name} ${scope} preferred value`} value={preferred} max={scaleMax} step={step} onChange={(value) => onChange(index, preferredKey, value)} />
                          <SizeInput label={`${tier.name} ${scope} maximum value`} value={high} max={scaleMax} step={step} onChange={(value) => onChange(index, maxKey, value)} />
                        </div>
                      );
                    })
                  : null}
              </div>
            );
          })}
        </div>
      </div>
    </ListCard>
  );
}

/** The two bands people actually tune; the rest open on demand. */
const DEFAULT_OPEN_BANDS = ["1080p", "2160p"];

/** Resolution bands, so a 26-row table can be scanned instead of scrolled. */
function groupTiers(tiers: QualityTierDefinition[], minKey: keyof QualityTierDefinition, maxKey: keyof QualityTierDefinition) {
  const bands: { label: string; test: (rank: number) => boolean }[] = [
    { label: "Low quality", test: (rank) => rank < 10 },
    { label: "SD", test: (rank) => rank >= 10 && rank < 30 },
    { label: "720p", test: (rank) => rank >= 30 && rank < 60 },
    { label: "1080p", test: (rank) => rank >= 60 && rank < 95 },
    { label: "2160p", test: (rank) => rank >= 95 && rank < 125 },
    { label: "Disc and raw", test: (rank) => rank >= 125 }
  ];
  const indexed = tiers.map((tier, index) => ({ tier, index }));
  return bands
    .map((band) => {
      const entries = indexed.filter((entry) => band.test(entry.tier.rank));
      // A tier set to 0/0 carries no rule, so it must not drag the band's summary to "Unlimited".
      const ruled = entries.filter((entry) => Number(entry.tier[minKey]) !== 0 || Number(entry.tier[maxKey]) !== 0);
      const mins = ruled.map((entry) => Number(entry.tier[minKey]));
      const maxes = ruled.map((entry) => Number(entry.tier[maxKey]));
      return {
        label: band.label,
        tiers: entries,
        spanMin: ruled.length ? Math.min(...mins) : 0,
        // A 0 maximum on a ruled tier means that tier, and so the band, has no ceiling.
        spanMax: maxes.includes(0) ? 0 : ruled.length ? Math.max(...maxes) : 0,
        unruled: ruled.length === 0
      };
    })
    .filter((band) => band.tiers.length > 0);
}

function round(value: number, step: number) {
  const decimals = step < 1 ? 1 : 0;
  return Number(value.toFixed(decimals));
}

function SizeInput({ label, value, max, step, onChange }: { label: string; value: number; max: number; step: number; onChange: (value: number) => void }) {
  return (
    <Field label={label} hideLabel>
      <Input type="number" min={0} max={max} step={step} value={value} onChange={(event) => onChange(Number(event.target.value || 0))} className="px-2 text-right tabular-nums" />
    </Field>
  );
}
