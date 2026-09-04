import { useEffect, useId, useMemo, useRef, useState } from "react";
import { Check, ChevronDown } from "lucide-react";
import { Chip } from "../ui/chip";
import { Input } from "../ui/input";
import { cn } from "../../lib/utils";
import { judgeDraftProfile, type DraftProfileJudgement } from "../../lib/api";
import {
  QUALITY_STEPS,
  describeAnswer,
  formatsForStep,
  type QualityStep
} from "../../lib/quality-steps";
import type { CustomFormatItem, GuidePackage } from "../../lib/api";

/**
 * A quality profile as seven answerable questions.
 *
 * <p><b>The steps are the editor, permanently.</b> Not a first-run flow that
 * drops you into six tabs afterwards. Come back to a saved profile and the same
 * seven questions are a checklist of your answers; click the one you want to
 * change and it opens in place, with the other six still readable above and
 * below it. That is what stops this being a wizard — a wizard is a thing you
 * are inside, and this is a thing you look at.</p>
 *
 * <p><b>Nothing ever starts empty.</b> Every step opens with an answer already
 * chosen, so somebody who clicks straight through gets a good profile rather
 * than an inert one. It is also what retires the scenario picker: a scenario
 * was only ever a named set of answers to these questions.</p>
 *
 * <p><b>You watch it judge while you build.</b> The release preview used to be
 * a "Test a release" button off to one side, which is the wrong place for it —
 * by the time you go looking for it you have already decided. It lives inside
 * every step: change the sound answer and the release above it flips, with the
 * reason.</p>
 */
export interface QualityBuildStepsProps {
  mediaType: "movies" | "tv";
  name: string;
  allowed: string[];
  cutoff: string;
  customFormatIds: string[];
  /** How much this profile cares about each selected preference, by format id. */
  formatIntents: Record<string, string>;
  upgradeUntilCutoff: boolean;
  upgradeUnknownItems: boolean;
  customFormats: CustomFormatItem[];
  guide: GuidePackage;
  onCustomFormatIdsChange: (ids: string[]) => void;
  onFormatIntentChange: (formatId: string, intent: string) => void;
  /** Step 1 and step 2 own controls that live outside the format list. */
  renderQualityControls: () => React.ReactNode;
  renderSizeControls: () => React.ReactNode;
  /** Advanced fields for the step that owns them, opened in place. */
  renderAdvanced?: (step: QualityStep) => React.ReactNode;
}

export function QualityBuildSteps(props: QualityBuildStepsProps) {
  const [openStep, setOpenStep] = useState<string | null>(QUALITY_STEPS[0].id);

  return (
    <div className="grid gap-2" role="list" aria-label="Build this profile">
      {QUALITY_STEPS.map((step) => (
        <StepRow
          key={step.id}
          step={step}
          open={openStep === step.id}
          onOpenChange={(open) => setOpenStep(open ? step.id : null)}
          {...props}
        />
      ))}
    </div>
  );
}

function StepRow({
  step,
  open,
  onOpenChange,
  ...props
}: QualityBuildStepsProps & {
  step: QualityStep;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const bodyId = useId();
  const offered = useMemo(
    () =>
      formatsForStep(step, props.customFormats, props.guide).filter(
        (format) => format.mediaType === props.mediaType
      ),
    [step, props.customFormats, props.guide, props.mediaType]
  );
  const chosen = useMemo(
    () => props.customFormatIds.filter((id) => offered.some((format) => format.id === id)),
    [props.customFormatIds, offered]
  );

  const answer =
    step.id === "quality"
      ? describeQualityAnswer(props.allowed, props.cutoff)
      : step.id === "size"
        ? describeSizeAnswer(props.allowed.length)
        : describeAnswer(step, chosen, offered);

  function toggle(id: string) {
    props.onCustomFormatIdsChange(
      props.customFormatIds.includes(id)
        ? props.customFormatIds.filter((current) => current !== id)
        : [...props.customFormatIds, id]
    );
  }

  return (
    <div
      role="listitem"
      className={cn(
        "overflow-hidden rounded-[10px] border bg-surface-2/50 transition-colors",
        open ? "border-ring/60" : "border-hairline"
      )}
    >
      <button
        type="button"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={() => onOpenChange(!open)}
        className="flex w-full items-start gap-3 px-[var(--field-pad-x)] py-3 text-left transition-colors hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring"
      >
        <span
          aria-hidden
          className="mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-surface-3 text-[length:var(--type-caption)] font-medium text-muted-foreground"
        >
          {step.number}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block text-[length:var(--type-body)] font-medium">{step.question}</span>
          {/* The answer, always. A step you have not opened still tells you
              what it decided on your behalf. */}
          <span className="mt-0.5 flex items-center gap-1.5 text-[length:var(--type-caption)] text-muted-foreground">
            <Check aria-hidden className="h-3.5 w-3.5 text-success" />
            {answer}
          </span>
        </span>
        <ChevronDown
          aria-hidden
          className={cn("mt-1 h-4 w-4 shrink-0 text-muted-foreground transition-transform", open && "rotate-180")}
        />
      </button>

      {open ? (
        <div id={bodyId} className="grid gap-[var(--grid-gap)] border-t border-hairline px-[var(--field-pad-x)] py-3">
          <p className="text-[length:var(--type-caption)] text-muted-foreground">{step.purpose}</p>

          {step.id === "quality" ? props.renderQualityControls() : null}
          {step.id === "size" ? props.renderSizeControls() : null}

          {offered.length ? (
            <div role="group" aria-label={step.question} className="flex flex-wrap gap-1.5">
              {offered.map((format) => {
                const on = props.customFormatIds.includes(format.id);
                return (
                  <button
                    key={format.id}
                    type="button"
                    role="checkbox"
                    aria-checked={on}
                    onClick={() => toggle(format.id)}
                    className={cn(
                      "rounded-full border px-2.5 py-1 text-[length:var(--type-caption)] transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                      on
                        ? "border-ring bg-ring/15 text-foreground"
                        : "border-hairline text-muted-foreground hover:border-ring/50 hover:text-foreground"
                    )}
                  >
                    {format.name}
                  </button>
                );
              })}
            </div>
          ) : null}

          {/* #394: how much, not only whether. Two shelves that both want HDR
              must be able to disagree about whether it is a nice-to-have or the
              whole point — and the answer is words, because an unbounded score
              is exactly what #353 took off this surface. */}
          {chosen.length ? (
            <ul className="grid gap-1.5">
              {chosen.map((formatId) => {
                const format = offered.find((candidate) => candidate.id === formatId);
                if (!format) return null;
                const intent = props.formatIntents[formatId] ?? guideIntentFor(format.score);
                return (
                  <li key={formatId} className="flex flex-wrap items-center justify-between gap-2">
                    <span className="text-[length:var(--type-caption)]">{format.name}</span>
                    <div role="radiogroup" aria-label={`How much this profile wants ${format.name}`} className="flex gap-1">
                      {INTENT_CHOICES.map((choice) => (
                        <button
                          key={choice.value}
                          type="button"
                          role="radio"
                          aria-checked={intent === choice.value}
                          title={choice.help}
                          onClick={() => props.onFormatIntentChange(formatId, choice.value)}
                          className={cn(
                            "rounded-md border px-2 py-0.5 text-[length:var(--type-caption)] transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                            intent === choice.value
                              ? "border-ring bg-ring/15 text-foreground"
                              : "border-hairline text-muted-foreground hover:text-foreground"
                          )}
                        >
                          {choice.label}
                        </button>
                      ))}
                    </div>
                  </li>
                );
              })}
            </ul>
          ) : null}

          {props.renderAdvanced?.(step) ?? null}

          <StepJudgement step={step} {...props} />
        </div>
      ) : null}
    </div>
  );
}

/**
 * The live judgement, inside the step.
 *
 * <p>Judged against the answers as they stand rather than against the last
 * saved profile, which is the whole point — a preview that reflects what you
 * saved last week cannot show you what this change does.</p>
 */
function StepJudgement(props: QualityBuildStepsProps & { step: QualityStep }) {
  const [releaseName, setReleaseName] = useState(() => exampleFor(props.mediaType));
  const [verdict, setVerdict] = useState<DraftProfileJudgement | null>(null);
  const [failed, setFailed] = useState(false);
  const requestId = useRef(0);

  const body = useMemo(
    () => ({
      name: props.name,
      mediaType: props.mediaType,
      allowedQualities: props.allowed,
      cutoffQuality: props.cutoff,
      customFormatIds: props.customFormatIds,
      formatIntents: props.formatIntents,
      upgradeUntilCutoff: props.upgradeUntilCutoff,
      upgradeUnknownItems: props.upgradeUnknownItems,
      allowLowerQualityReplacements: false,
      releaseName
    }),
    [
      props.name,
      props.mediaType,
      props.allowed,
      props.cutoff,
      props.customFormatIds,
      props.formatIntents,
      props.upgradeUntilCutoff,
      props.upgradeUnknownItems,
      releaseName
    ]
  );

  useEffect(() => {
    if (!releaseName.trim()) {
      setVerdict(null);
      return;
    }

    // Debounced, because this fires on every keystroke in the release name and
    // on every chip toggle above it.
    const id = ++requestId.current;
    const timer = window.setTimeout(async () => {
      try {
        const judgement = await judgeDraftProfile(body);
        // Only the newest answer wins. Two edits in flight would otherwise
        // render whichever server replied last.
        if (id === requestId.current) {
          setVerdict(judgement);
          setFailed(false);
        }
      } catch {
        if (id === requestId.current) setFailed(true);
      }
    }, 350);

    return () => window.clearTimeout(timer);
  }, [body, releaseName]);

  const status = verdict?.candidateEvaluation?.status;
  // The allowed list is a gate. When it refuses, nothing the preference
  // evaluation found can outrank that, so it is what the panel says.
  const refusal = verdict?.refusal;
  const tone = refusal ? "bad" : verdictTone(status);

  return (
    <div className="grid gap-2 rounded-[10px] border border-hairline bg-surface-1 p-3">
      <label className="grid gap-1.5">
        <span className="text-[length:var(--type-caption)] font-medium">Try a release against these answers</span>
        <Input
          value={releaseName}
          onChange={(event) => setReleaseName(event.target.value)}
          placeholder="Paste a release name"
          aria-label="Release name to judge"
        />
      </label>

      {failed ? (
        <p className="text-[length:var(--type-caption)] text-muted-foreground">
          Deluno could not judge that just now. Change an answer to try again.
        </p>
      ) : verdict ? (
        <div className="grid gap-1.5">
          <Chip tone={tone}>{refusal ? "Deluno would not take this" : verdictWords(status)}</Chip>
          <ul className="grid gap-1 text-[length:var(--type-caption)] text-muted-foreground">
            {refusal ? (
              <li>{refusal}</li>
            ) : (
              (verdict.candidateEvaluation?.reasons ?? []).slice(0, 4).map((reason) => (
                <li key={reason}>{reason}</li>
              ))
            )}
          </ul>
        </div>
      ) : (
        <p className="text-[length:var(--type-caption)] text-muted-foreground">
          Type a release name and Deluno will say what it would do with it.
        </p>
      )}
    </div>
  );
}

/**
 * In words, because "meetsPlan" is Deluno talking to itself.
 *
 * <p>Matched case-insensitively, and deliberately. The first version compared
 * against `"MeetsPlan"` while the API serialises `"meetsPlan"`, so every verdict
 * fell through to the default and the panel said <i>"Deluno would refuse this"</i>
 * underneath a reason that read "All upgrade-driving targets are met". A
 * serialiser that changes its casing must not be able to turn every answer into
 * the most alarming one.</p>
 */
export function verdictWords(status: string | undefined): string {
  switch (status?.toLowerCase()) {
    case "meetsplan":
      return "Deluno would take this and stop looking";
    case "belowgoal":
      return "Deluno would take this and keep looking for better";
    case "needsreview":
      return "Deluno cannot tell from the name alone";
    case "missing":
      return "Deluno would refuse this";
    default:
      // Not a refusal. An answer Deluno does not recognise is an answer it
      // cannot report, and saying "refused" would be inventing a verdict.
      return "Deluno could not say";
  }
}

export function verdictTone(status: string | undefined): "ok" | "warn" | "bad" | "idle" {
  switch (status?.toLowerCase()) {
    case "meetsplan":
      return "ok";
    case "belowgoal":
      return "warn";
    case "needsreview":
      return "warn";
    case "missing":
      return "bad";
    default:
      return "idle";
  }
}

/**
 * Counted rather than listed. Three tiers each with a range is four numbers a
 * row cannot hold, and the point of the checklist line is to say the step has
 * been answered - the sliders below say what the answer is.
 */
/**
 * The same five words the release-rules list uses, so somebody who has read one
 * of these screens has read both.
 */
const INTENT_CHOICES = [
  { value: "blocked", label: "Never", help: "Deluno will never take a release that matches this." },
  { value: "avoid", label: "Avoid", help: "Taken only when nothing better is available." },
  { value: "neutral", label: "Don't mind", help: "Makes no difference to which release Deluno picks." },
  { value: "prefer", label: "Prefer", help: "Tips a close call towards releases that match." },
  { value: "strong-prefer", label: "Must have", help: "Weighs heavily in favour, and can justify replacing a file you already have." }
];

/**
 * What the guide recommends for a preference this profile has not answered for.
 *
 * <p>The same thresholds the backend reads, because a profile showing "Prefer"
 * while the engine treats it as "Must have" is worse than showing nothing.</p>
 */
export function guideIntentFor(score: number): string {
  if (score <= -10000) return "blocked";
  if (score < 0) return "avoid";
  if (score === 0) return "neutral";
  if (score >= 500) return "strong-prefer";
  return "prefer";
}

function describeSizeAnswer(tierCount: number): string {
  if (tierCount === 0) return "Nothing chosen yet";
  return tierCount === 1 ? "Your own size for 1 tier" : `Your own size for each of ${tierCount} tiers`;
}

function describeQualityAnswer(allowed: string[], cutoff: string): string {
  if (allowed.length === 0) return "No tiers chosen yet";

  const best = [...allowed].reverse();
  const ladder = best.length <= 3 ? best.join(" → ") : `${best.slice(0, 3).join(" → ")} → …`;
  return cutoff ? `${ladder}, stops at ${cutoff}` : ladder;
}

/** A release worth judging, so the panel is never blank on open. */
function exampleFor(mediaType: "movies" | "tv"): string {
  return mediaType === "tv"
    ? "The.Expanse.S05E01.2160p.AMZN.WEB-DL.DDP5.1.HDR.HEVC-NTb"
    : "Dune.2021.2160p.UHD.BluRay.REMUX.HDR.TrueHD.Atmos-FraMeSToR";
}
