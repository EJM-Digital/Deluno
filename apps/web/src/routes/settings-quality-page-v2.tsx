/**
 * Size rules — a page-level form on the shared grammar.
 *
 *   PageToolbar (Media Plans tabs)
 *   ListCard × 2 (movie and episode guardrails, one row per quality tier)
 *   ListCard (upgrade behaviour)
 *   PageFooter (sticky: status · Discard · Save)
 *
 * Contracts: GET/PUT /api/quality-model.
 */
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useLoaderData } from "react-router-dom";
import { Field } from "../components/ui/field";
import { Input } from "../components/ui/input";
import { ListCard } from "../components/ui/list-card";
import { PageFooter } from "../components/ui/page-footer";
import { PageToolbar } from "../components/ui/page-toolbar";
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
      <PageToolbar tabs={TABS} />

      <p className="max-w-[80ch] text-[length:var(--type-body-sm)] text-muted-foreground">
        Quality decides; size protects. A quality profile chooses the allowed release tiers and the upgrade target — these limits are the final sanity check that rejects files which are implausibly small or large. They apply across every library, so adjust them only when Deluno is accepting junk or turning down legitimate releases.
      </p>

      <SizeCard title="Movie file sizes" scope="movie" unit="GB" caption="Whole-film size per quality tier." tiers={model.tiers} minKey="movieMinGb" maxKey="movieMaxGb" step={0.1} onChange={updateTier} />
      <SizeCard title="Episode file sizes" scope="episode" unit="MB" caption="Per-episode size per quality tier." tiers={model.tiers} minKey="episodeMinMb" maxKey="episodeMaxMb" step={100} onChange={updateTier} />

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
  // One scale for every row in this table: bands become comparable down the column,
  // and the two thumbs of a row finally sit on the same ruler.
  const scaleMax = useMemo(() => niceCeiling(Math.max(...tiers.map((tier) => Number(tier[maxKey])), 1)), [tiers, maxKey]);
  // 0 as a maximum means "no upper limit", matching the backend's convention.
  const format = (value: number) => (unit === "GB" ? `${value} GB` : `${value.toLocaleString()} MB`);
  const formatMax = (value: number) => (value === 0 ? "Unlimited" : format(value));

  return (
    <ListCard title={title} count={`${caption} Values in ${unit} on a shared 0–${scaleMax.toLocaleString()} scale; a maximum of 0 means unlimited.`}>
      <div className="overflow-x-auto">
        <div className="min-w-[44rem]">
          <div className="grid grid-cols-[13rem_minmax(10rem,1fr)_7rem_7rem] items-center gap-[var(--grid-gap)] border-b border-hairline bg-surface-2/40 px-[var(--card-pad-x)]">
            {["Quality tier", "Accepted range", `Min (${unit})`, `Max (${unit})`].map((label, index) => (
              <span key={label} className={cn("h-[var(--list-thead-height)] leading-[var(--list-thead-height)] text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground", index >= 2 && "text-right")}>
                {label}
              </span>
            ))}
          </div>
          {tiers.map((tier, index) => {
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
                  scaleMax={scaleMax}
                  zeroMaxIsUnlimited
                  minLabel={`${tier.name} ${scope} minimum`}
                  maxLabel={`${tier.name} ${scope} maximum`}
                  onChange={(next) => {
                    if (next.min !== low) onChange(index, minKey, round(next.min, step));
                    if (next.max !== high) onChange(index, maxKey, round(next.max, step));
                  }}
                />
                <SizeInput label={`${tier.name} ${scope} minimum value`} value={low} max={scaleMax} step={step} onChange={(value) => onChange(index, minKey, value)} />
                <SizeInput label={`${tier.name} ${scope} maximum value`} value={high} max={scaleMax} step={step} onChange={(value) => onChange(index, maxKey, value)} />
              </div>
            );
          })}
        </div>
      </div>
    </ListCard>
  );
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
