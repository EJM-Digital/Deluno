import { ArrowRight, CheckCircle2, Circle, CircleX, Sparkles } from "lucide-react";
import { Link } from "react-router-dom";
import type { SetupStatusModel, SetupStatusStep } from "../../lib/setup-status";
import { cn } from "../../lib/utils";

export function SetupProgressLadder({ status }: { status: SetupStatusModel }) {
  const heading = status.isComplete ? "All required configuration complete" : "Complete your Deluno setup";
  const count = `${status.completedCount}/${status.totalCount} required steps complete`;
  const currentStepId = status.steps.find((step) => !step.optional && !step.complete)?.id;

  return (
    <section
      aria-label="Setup progress"
      className="relative overflow-hidden rounded-2xl border border-hairline bg-card p-4 shadow-card dark:border-white/[0.06] sm:p-5"
    >
      <div aria-hidden className="pointer-events-none absolute -right-20 -top-24 h-64 w-64 rounded-full bg-primary/10 blur-[80px]" />
      <div className="relative">
        <div className="grid min-w-0 gap-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-start">
          <div className="min-w-0">
            <p className="flex items-center gap-2 text-[length:var(--type-caption)] font-bold uppercase tracking-[0.18em] text-primary">
              <Sparkles className="h-3.5 w-3.5" />
              Setup progress
            </p>
            <h2 className="mt-0.5 font-display text-[length:var(--type-title-sm)] font-semibold leading-tight tracking-tight text-foreground">{heading}</h2>
            <p className="mt-1 max-w-3xl text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">{status.summary}</p>
          </div>
          <span className="tabular justify-self-start whitespace-nowrap rounded-full border border-hairline bg-surface-2 px-2.5 py-1 text-[length:var(--type-caption)] font-semibold text-muted-foreground sm:justify-self-end">
            {count}
          </span>
        </div>

        <ol className="mt-4 grid min-w-0 gap-2 sm:grid-cols-2 2xl:flex 2xl:items-stretch 2xl:gap-0 2xl:overflow-x-auto 2xl:pb-1">
          {status.steps.map((step, index) => {
            const nextStep = status.steps[index + 1];
            const nextRequiredStep = status.steps.slice(index + 1).find((candidate) => !candidate.optional);
            const leadsToCurrent = Boolean(nextRequiredStep && step.complete && nextRequiredStep.id === currentStepId);

            return (
            <li key={step.id} className="flex min-w-0 flex-col items-stretch 2xl:min-w-[11.5rem] 2xl:flex-1 2xl:flex-row first:pl-0 last:pr-0">
              <Link
                to={step.to}
                aria-label={`${step.number}. ${step.title}: ${stepStateLabel(step)}`}
                className={cn(
                  "group relative flex min-w-0 flex-1 items-center gap-2.5 rounded-xl border px-2.5 py-2 transition-[background-color,border-color,box-shadow] duration-200 dark:bg-white/[0.02]",
                  step.state === "complete"
                    ? "border-success/30 bg-success/[0.06] hover:border-success/50 hover:bg-success/[0.1]"
                    : step.state === "failed"
                      ? "border-destructive/30 bg-destructive/[0.05] hover:border-destructive/50 hover:bg-destructive/[0.09]"
                      : step.id === currentStepId
                        ? "border-primary/35 bg-primary/[0.07] shadow-[inset_0_0_0_1px_hsl(var(--primary)/0.08)] hover:border-primary/55 hover:bg-primary/[0.1]"
                        : "border-hairline bg-surface-1/70 hover:border-primary/35 hover:bg-primary/[0.05]"
                )}
              >
                <StepStateIcon step={step} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground">{step.number}. {step.title}</span>
                  <span className={cn(
                    "mt-0.5 block truncate text-[length:var(--type-caption)] leading-snug",
                    step.state === "failed" ? "text-destructive" : step.state === "complete" ? "text-success" : "text-muted-foreground"
                  )}>
                    {step.optional && step.state !== "complete" ? "Optional · " : ""}{stepStateLabel(step)}
                  </span>
                  <span className="mt-1 flex items-center gap-1 truncate text-[length:var(--type-micro)] font-bold uppercase tracking-[0.12em] text-primary">
                    {step.action}
                    <ArrowRight className="h-3 w-3 shrink-0 transition-transform group-hover:translate-x-0.5" />
                  </span>
                </span>
              </Link>
              {nextStep ? (
                <span aria-hidden="true" className={cn("flex h-7 w-full shrink-0 items-center justify-center 2xl:h-auto 2xl:w-7", leadsToCurrent ? "text-success" : "text-muted-foreground/30")}>
                  <ArrowRight className={cn("h-4 w-4 rotate-90 2xl:rotate-0", leadsToCurrent && "motion-safe:animate-pulse")} />
                </span>
              ) : null}
            </li>
            );
          })}
        </ol>
      </div>
    </section>
  );
}

function StepStateIcon({ step }: { step: SetupStatusStep }) {
  const Icon = step.state === "complete" ? CheckCircle2 : step.state === "failed" ? CircleX : Circle;
  const tone = step.state === "complete" ? "border-success/30 bg-success/10 text-success" : step.state === "failed" ? "border-destructive/30 bg-destructive/10 text-destructive" : "border-hairline bg-surface-2 text-muted-foreground/50";
  return (
    <span className={cn("relative z-10 flex h-8 w-8 shrink-0 items-center justify-center rounded-full border", tone)}>
      <Icon className="h-4 w-4" strokeWidth={1.9} aria-hidden />
    </span>
  );
}

function stepStateLabel(step: SetupStatusStep) {
  if (step.state === "complete") return step.status;
  if (step.state === "failed") return `Needs attention · ${step.status}`;
  return step.status;
}
