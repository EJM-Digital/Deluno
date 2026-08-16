import { useState } from "react";
import { useLoaderData } from "react-router-dom";
import { SettingsShell } from "../components/app/settings-shell";
import { Button } from "../components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { Input } from "../components/ui/input";
import { RouteSkeleton } from "../components/shell/skeleton";
import { fetchJson, type QualityModelSnapshot, type QualityTierDefinition } from "../lib/api";

interface QualityLoaderData {
  qualityModel: QualityModelSnapshot;
}

export async function settingsQualityLoader(): Promise<QualityLoaderData> {
  return { qualityModel: await fetchJson<QualityModelSnapshot>("/api/quality-model") };
}

export function SettingsQualityPage() {
  const loaderData = useLoaderData() as QualityLoaderData | undefined;
  if (!loaderData) return <RouteSkeleton />;

  const [qualityModel, setQualityModel] = useState(loaderData.qualityModel);
  const [message, setMessage] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  return (
    <SettingsShell
      title="Size rules"
      description="Usually leave these at their balanced defaults. Media Plans and quality profiles decide what Deluno wants; these rules reject files that are implausibly small or large."
    >
      <Card className="border-primary/20 bg-gradient-to-r from-primary/[0.07] via-primary/[0.025] to-transparent">
        <CardHeader>
          <CardTitle>Quality decides; size protects</CardTitle>
          <CardDescription>
            A quality profile chooses allowed release tiers and an upgrade target. These limits are a final sanity check before Deluno accepts a release. They apply across your libraries, so you only need to adjust them when your storage or source material has unusual requirements.
          </CardDescription>
        </CardHeader>
      </Card>

      {message ? <p className="rounded-xl border border-hairline bg-surface-1 px-3 py-2 text-sm text-muted-foreground">{message}</p> : null}

      <SizeLimitsCard
        title="Movies"
        description="Final file-size checks for movies. Values are in GB."
        mediaType="movies"
        tiers={qualityModel.tiers}
        onChange={(index, key, value) => setQualityModel((current) => updateTierValue(current, index, key, value))}
      />
      <SizeLimitsCard
        title="TV shows"
        description="Final file-size checks for individual episodes. Values are in MB."
        mediaType="tv"
        tiers={qualityModel.tiers}
        onChange={(index, key, value) => setQualityModel((current) => updateTierValue(current, index, key, value))}
      />
      <p className="text-xs leading-relaxed text-muted-foreground">Drag a slider for a sensible range, or type an exact number when needed. These limits are protection, not quality preferences: each library’s selected quality profile still decides what Deluno aims to find.</p>

      <Card className="settings-panel">
        <CardHeader>
          <CardTitle>Upgrade behaviour</CardTitle>
          <CardDescription>These rules refine what happens after a file is already imported. Most libraries should keep the defaults.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-[var(--grid-gap)]">
          <div className="grid gap-[var(--grid-gap)] sm:grid-cols-2">
              <ToggleField
                label="Stop at the profile cutoff"
                description="Stop upgrades when the current file has reached the quality profile’s target tier."
                checked={qualityModel.upgradeStop.stopWhenCutoffMet}
                onChange={(checked) => setQualityModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, stopWhenCutoffMet: checked } }))}
              />
              <ToggleField
                label="Require a score improvement at the same quality"
                description="Only replace an equally ranked release when its release-scoring result is better."
                checked={qualityModel.upgradeStop.requireCustomFormatGainForSameQuality}
                onChange={(checked) => setQualityModel((current) => ({ ...current, upgradeStop: { ...current.upgradeStop, requireCustomFormatGainForSameQuality: checked } }))}
              />
          </div>
          <Button type="button" disabled={saving} onClick={() => void saveModel(qualityModel, setSaving, setMessage, setQualityModel)}>
            {saving ? "Saving…" : "Save file-size guardrails"}
          </Button>
        </CardContent>
      </Card>
    </SettingsShell>
  );
}

function SizeLimitsCard({
  title,
  description,
  mediaType,
  tiers,
  onChange
}: {
  title: string;
  description: string;
  mediaType: "movies" | "tv";
  tiers: QualityTierDefinition[];
  onChange: (index: number, key: keyof QualityTierDefinition, value: number) => void;
}) {
  const isMovies = mediaType === "movies";
  const minKey: keyof QualityTierDefinition = isMovies ? "movieMinGb" : "episodeMinMb";
  const maxKey: keyof QualityTierDefinition = isMovies ? "movieMaxGb" : "episodeMaxMb";
  const unit = isMovies ? "GB" : "MB";
  const minimumMaximum = isMovies ? 50 : 10000;
  const maximumMinimum = isMovies ? 1 : 100;
  const maximumMaximum = isMovies ? 200 : 50000;
  const step = isMovies ? 0.1 : 100;

  return (
    <Card className="settings-panel">
      <CardHeader>
        <CardTitle>{title} file-size guardrails</CardTitle>
        <CardDescription>{description} Adjust only when Deluno is accepting obvious junk or rejecting legitimate releases.</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="overflow-x-auto rounded-2xl border border-hairline">
          <table className="w-full min-w-[34rem] border-collapse text-sm">
            <thead className="bg-surface-1 text-left text-xs font-bold uppercase tracking-[0.14em] text-muted-foreground">
              <tr>
                <th className="px-[var(--tile-pad)] py-3">Quality tier</th>
                <th className="border-l border-hairline px-[var(--tile-pad)] py-3">Minimum ({unit})</th>
                <th className="border-l border-hairline px-[var(--tile-pad)] py-3">Maximum ({unit})</th>
              </tr>
            </thead>
            <tbody>
              {tiers.map((tier, index) => (
                <tr key={tier.name} className="border-t border-hairline transition-colors hover:bg-muted/20">
                  <td className="px-[var(--tile-pad)] py-3"><span className="font-semibold text-foreground">{tier.name}</span><span className="ml-2 text-xs text-muted-foreground">Rank {tier.rank}</span></td>
                  <td className="border-l border-hairline px-[var(--tile-pad)] py-2"><SliderLimitInput label={`${tier.name} ${isMovies ? "movie" : "episode"} minimum`} value={tier[minKey] as number} min={0} max={minimumMaximum} step={step} unit={unit} onChange={(value) => onChange(index, minKey, value)} /></td>
                  <td className="border-l border-hairline px-[var(--tile-pad)] py-2"><SliderLimitInput label={`${tier.name} ${isMovies ? "movie" : "episode"} maximum`} value={tier[maxKey] as number} min={maximumMinimum} max={maximumMaximum} step={isMovies ? 0.5 : 100} unit={unit} onChange={(value) => onChange(index, maxKey, value)} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CardContent>
    </Card>
  );
}

function SliderLimitInput({
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
    <div className="min-w-32 space-y-2">
      <input
        aria-label={label}
        className="h-2 w-full cursor-pointer appearance-none rounded-full bg-muted accent-primary"
        type="range"
        min={min}
        max={max}
        step={step}
        value={sliderValue}
        onChange={(event) => onChange(Number(event.target.value))}
      />
      <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <span className="sr-only">{label}</span>
        <Input className="h-8 min-w-0 px-2 text-right" type="number" min={min} max={max} step={step} value={value} onChange={(event) => onChange(Number(event.target.value || 0))} />
        <span>{unit}</span>
      </label>
    </div>
  );
}

function ToggleField({ checked, description, label, onChange }: { checked: boolean; description: string; label: string; onChange: (checked: boolean) => void }) {
  return (
    <label className="flex items-start gap-3 rounded-xl border border-hairline bg-background/40 p-3 text-foreground">
      <input className="mt-1" type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span>
        <span className="block text-sm font-semibold">{label}</span>
        <span className="mt-1 block text-sm leading-relaxed text-muted-foreground">{description}</span>
      </span>
    </label>
  );
}

function updateTierValue(model: QualityModelSnapshot, index: number, key: keyof QualityTierDefinition, value: number): QualityModelSnapshot {
  const tiers = model.tiers.map((tier, tierIndex) => tierIndex === index ? { ...tier, [key]: Number.isFinite(value) ? value : 0 } : tier);
  return { ...model, tiers };
}

async function saveModel(
  model: QualityModelSnapshot,
  setSaving: (value: boolean) => void,
  setMessage: (value: string | null) => void,
  setQualityModel: (value: QualityModelSnapshot) => void
) {
  setSaving(true);
  setMessage(null);
  try {
    const saved = await fetchJson<QualityModelSnapshot>("/api/quality-model", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ tiers: model.tiers, upgradeStop: model.upgradeStop })
    });
    setQualityModel(saved);
    setMessage("Quality and size limits saved.");
  } catch (error) {
    setMessage(error instanceof Error ? error.message : "Could not save quality and size limits.");
  } finally {
    setSaving(false);
  }
}
