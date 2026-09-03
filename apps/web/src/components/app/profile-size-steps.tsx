import { RangeAxis, RangeSlider } from "../ui/range-slider";
import type { ProfileSizeRule, QualityTierDefinition } from "../../lib/api";

/**
 * Step 2: how big a file of each tier should be, <b>for this profile</b>.
 *
 * <p>#394. Size used to live on the tier, so a Low Storage profile and a
 * Premium 4K profile that both allowed WEB 1080p got the same range and
 * changing one changed the other silently. Anime at 1080p and a film at 1080p
 * are not the same number of gigabytes.</p>
 *
 * <p><b>Nothing is inherited, and the shared number is not a setting.</b> The
 * band behind each track is where files of that tier actually land — a physical
 * fact about the encode, drawn so somebody choosing 2–5 GB for anime can see
 * where films of that tier normally sit while they drag. Your handles are
 * yours; the band is only the ruler, and moving outside it is allowed.</p>
 *
 * <p>A slider cannot be blank, which is what settles the question a default
 * would otherwise raise: the handles have to start somewhere, so they start
 * where the band is, and those values are the profile's own from that moment.</p>
 */
export interface ProfileSizeStepsProps {
  mediaType: "movies" | "tv";
  /** Tiers this profile allows, best first. */
  allowed: string[];
  rules: ProfileSizeRule[];
  /**
   * Where files of each tier actually land, from the backend's own list rather
   * than a copy kept here. Two copies of a physical fact drift, and the one
   * nobody looks at is the one that decides whether a release is refused.
   */
  tiers: QualityTierDefinition[];
  onChange: (rules: ProfileSizeRule[]) => void;
}

/** One ruler for every row, so rows can be read down the column. */
const FILM_SCALE_MAX = 130;
const EPISODE_SCALE_MAX = 36_000;

export function ProfileSizeSteps({ mediaType, allowed, rules, tiers, onChange }: ProfileSizeStepsProps) {
  const typicalFor = (quality: string) => {
    const tier = tiers.find((candidate) => candidate.name.toLowerCase() === quality.toLowerCase());
    return {
      minGb: tier?.movieMinGb ?? 0.1,
      maxGb: tier?.movieMaxGb ?? 130,
      minMb: tier?.episodeMinMb ?? 50,
      maxMb: tier?.episodeMaxMb ?? 36_000
    };
  };

  const isFilm = mediaType === "movies";
  const scaleMax = isFilm ? FILM_SCALE_MAX : EPISODE_SCALE_MAX;
  const step = isFilm ? 0.1 : 50;

  if (allowed.length === 0) {
    return (
      <p className="text-[length:var(--type-caption)] text-muted-foreground">
        Answer the first question and the tiers you chose appear here, each with its own size.
      </p>
    );
  }

  function ruleFor(quality: string): ProfileSizeRule {
    const stored = rules.find((rule) => rule.quality.toLowerCase() === quality.toLowerCase());
    if (stored) return stored;

    const typical = typicalFor(quality);
    return {
      quality,
      minGb: typical.minGb,
      maxGb: typical.maxGb,
      minMb: typical.minMb,
      maxMb: typical.maxMb
    };
  }

  function set(quality: string, next: { min: number; max: number }) {
    const current = ruleFor(quality);
    const updated: ProfileSizeRule = isFilm
      ? { ...current, minGb: next.min, maxGb: next.max }
      : { ...current, minMb: next.min, maxMb: next.max };

    onChange([
      ...rules.filter((rule) => rule.quality.toLowerCase() !== quality.toLowerCase()),
      updated
    ]);
  }

  const format = (value: number) => (isFilm ? `${value.toFixed(1)} GB` : `${Math.round(value)} MB`);

  return (
    <div className="grid gap-3">
      <RangeAxis scaleMax={scaleMax} scale="sqrt" format={format} />
      <ul className="grid gap-3">
        {[...allowed].reverse().map((quality) => {
          const rule = ruleFor(quality);
          const typical = typicalFor(quality);
          const min = isFilm ? rule.minGb : rule.minMb;
          const max = isFilm ? rule.maxGb : rule.maxMb;

          return (
            <li key={quality} className="grid gap-1">
              <div className="flex items-baseline justify-between gap-2">
                <span className="text-[length:var(--type-body-sm)] font-medium">{quality}</span>
                <span className="text-[length:var(--type-caption)] tabular-nums text-muted-foreground">
                  {format(min)}–{max > 0 ? format(max) : "no limit"}
                </span>
              </div>
              <RangeSlider
                min={min}
                max={max}
                step={step}
                scaleMax={scaleMax}
                scale="sqrt"
                zeroMaxIsUnlimited
                typical={
                  isFilm
                    ? { min: typical.minGb, max: typical.maxGb }
                    : { min: typical.minMb, max: typical.maxMb }
                }
                formatValue={format}
                minLabel={`${quality} smallest file this profile accepts`}
                maxLabel={`${quality} largest file this profile accepts`}
                onChange={(next) => set(quality, next)}
              />
              <p className="text-[length:var(--type-caption)] text-muted-foreground">
                {describe(quality, min, max, isFilm, isFilm ? typical.minGb : typical.minMb, isFilm ? typical.maxGb : typical.maxMb)}
              </p>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/**
 * What this answer does, in words, while you drag it.
 *
 * <p>The same idea as watching a real release be judged while you build the
 * rest of the profile. A number on a track tells you what you chose; this tells
 * you what choosing it means.</p>
 */
export function describe(
  quality: string,
  min: number,
  max: number,
  isFilm: boolean,
  typicalMin: number,
  typicalMax: number
): string {
  const unit = isFilm ? "GB" : "MB";
  const thing = isFilm ? "films" : "episodes";

  if (min <= 0 && max <= 0) {
    return `Any size is accepted for ${quality}.`;
  }

  if (min <= typicalMin && (max <= 0 || max >= typicalMax)) {
    return `Accepts the whole range ${quality} ${thing} normally land in.`;
  }

  const tighterBelow = min > typicalMin;
  const tighterAbove = max > 0 && max < typicalMax;

  if (tighterBelow && tighterAbove) {
    return `Narrower than usual — ${quality} ${thing} outside ${min}–${max} ${unit} are refused.`;
  }

  if (tighterAbove) {
    return `Refuses ${quality} ${thing} over ${max} ${unit}, which the largest normally exceed.`;
  }

  if (tighterBelow) {
    return `Refuses ${quality} ${thing} under ${min} ${unit}, which rules out the smallest encodes.`;
  }

  return `Wider than ${quality} ${thing} normally need.`;
}
