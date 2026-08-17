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
import { SwitchRow } from "../components/ui/switch";
import { configurationNavAreas } from "../components/app/settings-shell";
import { useUnsavedChanges } from "../hooks/use-unsaved-changes";
import { fetchJson, type QualityModelSnapshot, type QualityTierDefinition } from "../lib/api";
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

      <SizeCard title="Movie file sizes" scope="movie" unit="GB" caption="Whole-film size per quality tier." tiers={model.tiers} minKey="movieMinGb" maxKey="movieMaxGb" minMax={50} maxMin={1} maxMax={200} step={0.1} maxStep={0.5} onChange={updateTier} />
      <SizeCard title="Episode file sizes" scope="episode" unit="MB" caption="Per-episode size per quality tier." tiers={model.tiers} minKey="episodeMinMb" maxKey="episodeMaxMb" minMax={10000} maxMin={100} maxMax={50000} step={100} maxStep={100} onChange={updateTier} />

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

function SizeCard({
  title,
  scope,
  caption,
  unit,
  tiers,
  minKey,
  maxKey,
  minMax,
  maxMin,
  maxMax,
  step,
  maxStep,
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
  minMax: number;
  maxMin: number;
  maxMax: number;
  step: number;
  maxStep: number;
  onChange: (index: number, key: keyof QualityTierDefinition, value: number) => void;
}) {
  return (
    <ListCard title={title} count={`${caption} Values in ${unit}.`}>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[36rem] border-collapse">
          <thead>
            <tr className="border-b border-hairline bg-surface-2/40">
              <th scope="col" className="h-[var(--list-thead-height)] px-[var(--card-pad-x)] text-left text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
                Quality tier
              </th>
              <th scope="col" className="h-[var(--list-thead-height)] px-[var(--card-pad-x)] text-left text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
                Minimum ({unit})
              </th>
              <th scope="col" className="h-[var(--list-thead-height)] px-[var(--card-pad-x)] text-left text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.1em] text-muted-foreground">
                Maximum ({unit})
              </th>
            </tr>
          </thead>
          <tbody>
            {tiers.map((tier, index) => (
              <tr key={tier.name} className="border-b border-hairline last:border-b-0">
                <th scope="row" className="min-h-[var(--list-row-height)] px-[var(--card-pad-x)] py-2 text-left align-middle">
                  <span className="block text-[length:var(--type-body-sm)] font-semibold text-foreground">{tier.name}</span>
                  <span className="block text-[length:var(--type-caption)] text-muted-foreground">Rank {tier.rank}</span>
                </th>
                <td className="px-[var(--card-pad-x)] py-2 align-middle">
                  <SizeInput label={`${tier.name} ${scope} minimum`} value={tier[minKey] as number} min={0} max={minMax} step={step} unit={unit} onChange={(value) => onChange(index, minKey, value)} />
                </td>
                <td className="px-[var(--card-pad-x)] py-2 align-middle">
                  <SizeInput label={`${tier.name} ${scope} maximum`} value={tier[maxKey] as number} min={maxMin} max={maxMax} step={maxStep} unit={unit} onChange={(value) => onChange(index, maxKey, value)} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </ListCard>
  );
}

function SizeInput({
  label,
  value,
  min,
  max,
  step,
  unit,
  onChange
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  unit: string;
  onChange: (value: number) => void;
}) {
  const sliderValue = Math.max(min, Math.min(max, value));
  return (
    <div className="flex min-w-[13rem] items-center gap-3">
      <input
        aria-label={`${label} slider`}
        type="range"
        min={min}
        max={max}
        step={step}
        value={sliderValue}
        onChange={(event) => onChange(Number(event.target.value))}
        className="h-1.5 flex-1 cursor-pointer appearance-none rounded-full bg-surface-3 accent-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      />
      <Field label={label} hideLabel className="w-[6.5rem] shrink-0">
        <span className="relative block">
          <Input type="number" min={min} max={max} step={step} value={value} onChange={(event) => onChange(Number(event.target.value || 0))} className="pr-9 text-right tabular-nums" />
          <span aria-hidden className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-[length:var(--type-caption)] text-muted-foreground">
            {unit}
          </span>
        </span>
      </Field>
    </div>
  );
}
