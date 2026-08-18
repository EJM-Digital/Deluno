/**
 * Size rules — a page-level form on the shared grammar.
 *
 *   PageToolbar (Media Plans tabs · Movies / TV)
 *   ListCard (one media type at a time, tiers grouped into collapsible
 *             resolution bands, each band on its own slider scale)
 *   ListCard (upgrade behaviour)
 *   PageFooter (pinned: status · Discard · Save)
 *
 * Contracts: GET/PUT /api/quality-model.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData } from "react-router-dom";
import { ChevronRight } from "lucide-react";
import { Button } from "../components/ui/button";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
import { SegmentedControl } from "../components/ui/segmented-control";
import { RangeSlider } from "../components/ui/range-slider";
import { SwitchRow } from "../components/ui/switch";
import { configurationNavAreas } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { fetchJson, type QualityModelSnapshot, type QualityTierDefinition } from "../lib/api";
import { cn } from "../lib/utils";
import type { DrawerSaveState } from "../components/ui/drawer";

const TABS = configurationNavAreas.find((area) => area.label === "Media Plans")?.items ?? [];

interface LoaderData {
  qualityModel: QualityModelSnapshot;
}

export async function settingsQualityLoader(): Promise<LoaderData> {
  return { qualityModel: await fetchJson<QualityModelSnapshot>("/api/quality-model") };
}

export function SettingsQualityPage() {
  const { qualityModel: loaded } = useLoaderData() as LoaderData;
  const [saved, setSaved] = useState(loaded);
  const [model, setModel] = useState(loaded);
  const [saveState, setSaveState] = useState<Exclude<DrawerSaveState, "clean" | "dirty">>();
  const [message, setMessage] = useState<string | null>(null);

  // One table at a time: 26 tiers × two units is a page nobody can scan.
  const [scope, setScope] = useState<"movie" | "episode">("movie");
  const dirty = useMemo(() => JSON.stringify(model) !== JSON.stringify(saved), [model, saved]);
  const state: DrawerSaveState = saveState === "saving" ? "saving" : dirty ? "dirty" : saveState ?? "clean";
  const blocker = useUnsavedChanges(dirty);

  useEffect(() => {
    if (dirty && (saveState === "saved" || saveState === "error")) setSaveState(undefined);
  }, [dirty, saveState]);

  // The blocker has no confirm dialog here: a page form discards nothing on its
  // own, so leaving is only confirmed through the browser prompt for reloads.
  useEffect(() => {
    if (blocker.state === "blocked" && !dirty) blocker.proceed();
  }, [blocker, dirty]);

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

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-[var(--page-gap)]" noValidate>
      <PageToolbar
        tabs={TABS}
        actions={
          <SegmentedControl<"movie" | "episode">
            aria-label="Show sizes for"
            className="w-auto"
            value={scope}
            onValueChange={setScope}
            options={[
              // "Movies · TV" is the vocabulary MediaTypeFilter already uses everywhere else.
              { value: "movie", label: "Movies" },
              { value: "episode", label: "TV" }
            ]}
          />
        }
      />

      {scope === "movie" ? (
        <SizeCard title="Movie file sizes" scope="movie" unit="GB" caption="Whole-film size per quality tier." tiers={model.tiers} minKey="movieMinGb" maxKey="movieMaxGb" step={0.1} onChange={updateTier} />
      ) : (
        <SizeCard title="Episode file sizes" scope="episode" unit="MB" caption="Per-episode size per quality tier." tiers={model.tiers} minKey="episodeMinMb" maxKey="episodeMaxMb" step={100} onChange={updateTier} />
      )}

      <ListCard title="Upgrade behaviour" count="What happens after a file is already in the library">
        <div className="grid gap-[var(--grid-gap)] p-[var(--card-pad-x)]">
          <SwitchRow
            label="Stop at the profile cutoff"
            description="Stop upgrading once the current file has reached the quality profile's target tier."
            checked={model.upgradeStop.stopWhenCutoffMet}
            onCheckedChange={(checked) => setModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, stopWhenCutoffMet: checked } }))}
          />
          <SwitchRow
            label="Require a score improvement at the same quality"
            description="Only replace an equally ranked release when its release score is better."
            checked={model.upgradeStop.requireCustomFormatGainForSameQuality}
            onCheckedChange={(checked) => setModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, requireCustomFormatGainForSameQuality: checked } }))}
          />
        </div>
      </ListCard>

      <PageFooter state={state} message={message} saveLabel="Save size rules" onDiscard={() => setModel(saved)} />
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

function SizeCard({
  title,
  scope,
  caption,
  unit,
  tiers,
  minKey,
  maxKey,
  step,
  onChange
}: {
  title: string;
  /** Disambiguates control labels between the movie and episode tables. */
  scope: "movie" | "episode";
  caption: string;
  unit: string;
  tiers: QualityTierDefinition[];
  minKey: keyof QualityTierDefinition;
  maxKey: keyof QualityTierDefinition;
  step: number;
  onChange: (index: number, key: keyof QualityTierDefinition, value: number) => void;
}) {
  const format = (value: number) => (unit === "GB" ? `${value} GB` : `${value.toLocaleString()} MB`);
  // 0 as a maximum means "no upper limit", matching the backend's convention.
  const formatMax = (value: number) => (value === 0 ? "Unlimited" : format(value));
  const bands = useMemo(() => groupTiers(tiers, minKey, maxKey), [tiers, minKey, maxKey]);
  const [open, setOpen] = useState<Record<string, boolean>>(() =>
    Object.fromEntries(bands.map((band) => [band.label, DEFAULT_OPEN_BANDS.includes(band.label)]))
  );
  const allOpen = bands.every((band) => open[band.label]);

  return (
    <ListCard
      title={title}
      count={`${caption} A maximum of 0 means unlimited.`}
      actions={
        <Button type="button" variant="outline" size="sm" onClick={() => setOpen(Object.fromEntries(bands.map((band) => [band.label, !allOpen])))}>
          {allOpen ? "Collapse all" : "Expand all"}
        </Button>
      }
    >
      <p className="border-b border-hairline px-[var(--card-pad-x)] py-3 text-[length:var(--type-body-sm)] text-muted-foreground">
        Quality decides; size protects. A quality profile chooses the allowed release tiers and the upgrade target — these limits are the
        final sanity check that rejects files which are implausibly small or large. They apply across every library, so adjust them only when
        Deluno is accepting junk or turning down legitimate releases.
      </p>

      <div className="overflow-x-auto">
        <div className="min-w-[44rem]">
          <div className="grid grid-cols-[13rem_minmax(10rem,1fr)_7rem_7rem] items-center gap-[var(--grid-gap)] border-b border-hairline bg-surface-2/40 px-[var(--card-pad-x)]">
            {["Quality tier", "Accepted range", `Min (${unit})`, `Max (${unit})`].map((label, index) => (
              <span key={label} className={cn("h-[var(--list-thead-height)] leading-[var(--list-thead-height)] text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground", index >= 2 && "text-right")}>
                {label}
              </span>
            ))}
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
                    "sticky top-0 z-10 flex w-full items-center gap-2 border-b border-hairline bg-surface-2/90 px-[var(--card-pad-x)] py-1.5 text-left backdrop-blur",
                    "text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground",
                    "transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
                  )}
                >
                  <ChevronRight aria-hidden className={cn("h-3.5 w-3.5 shrink-0 transition-transform", expanded && "rotate-90")} />
                  <span>{band.label}</span>
                  <span className="font-normal normal-case tracking-normal opacity-70">{band.tiers.length}</span>
                  <span className="flex-1" />
                  <span className="font-normal normal-case tracking-normal tabular-nums opacity-70">
                    {format(band.spanMin)}–{formatMax(band.spanMax)}
                  </span>
                </button>
                {expanded
                  ? band.tiers.map(({ tier, index }) => {
                      const low = Number(tier[minKey]);
                      const high = Number(tier[maxKey]);
                      return (
                        <div key={tier.name} className="grid min-h-[var(--list-row-height)] grid-cols-[13rem_minmax(10rem,1fr)_7rem_7rem] items-center gap-[var(--grid-gap)] border-b border-hairline px-[var(--card-pad-x)] last:border-b-0">
                          <div className="min-w-0">
                            <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{tier.name}</span>
                            <span className="block text-[length:var(--type-caption)] text-muted-foreground">Rank {tier.rank} · {format(low)}–{formatMax(high)}</span>
                          </div>
                          <RangeSlider
                            min={low}
                            max={high}
                            step={step}
                            scaleMax={band.scaleMax}
                            zeroMaxIsUnlimited
                            minLabel={`${tier.name} ${scope} minimum`}
                            maxLabel={`${tier.name} ${scope} maximum`}
                            onChange={(next) => {
                              if (next.min !== low) onChange(index, minKey, round(next.min, step));
                              if (next.max !== high) onChange(index, maxKey, round(next.max, step));
                            }}
                          />
                          <SizeInput label={`${tier.name} ${scope} minimum value`} value={low} max={band.scaleMax} step={step} onChange={(value) => onChange(index, minKey, value)} />
                          <SizeInput label={`${tier.name} ${scope} maximum value`} value={high} max={band.scaleMax} step={step} onChange={(value) => onChange(index, maxKey, value)} />
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

/**
 * Resolution bands, so a 26-row table can be scanned instead of scrolled.
 *
 * Each band carries its own slider scale. One table-wide scale is correct
 * arithmetic but useless to look at: a 0–150 GB ruler draws every SD and
 * low-quality tier as a sliver at the far left. Sizes are only ever compared
 * within a resolution anyway — nobody weighs a CAM against a 2160p remux.
 */
function groupTiers(tiers: QualityTierDefinition[], minKey: keyof QualityTierDefinition, maxKey: keyof QualityTierDefinition) {
  const bands: { label: string; test: (rank: number) => boolean }[] = [
    { label: "Low-quality sources", test: (rank) => rank < 10 },
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
      const mins = entries.map((entry) => Number(entry.tier[minKey]));
      const maxes = entries.map((entry) => Number(entry.tier[maxKey]));
      return {
        label: band.label,
        tiers: entries,
        // Minimums count too: with 0 meaning unlimited, a band's largest number can be a minimum.
        scaleMax: niceCeiling(Math.max(...mins, ...maxes, 1)),
        spanMin: entries.length ? Math.min(...mins) : 0,
        // A 0 anywhere in the band means the band as a whole has no ceiling.
        spanMax: maxes.includes(0) ? 0 : entries.length ? Math.max(...maxes) : 0
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
