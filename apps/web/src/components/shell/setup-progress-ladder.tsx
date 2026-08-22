import { ArrowRight, CheckCircle2, Circle, CircleX, Sparkles } from "lucide-react";
import { Link } from "react-router-dom";
import type { SetupStatusModel, SetupStatusStep } from "../../lib/setup-status";
import { cn } from "../../lib/utils";

export function SetupProgressLadder({ status }: { status: SetupStatusModel }) {
  const heading = status.isComplete ? "All required configuration complete" : "Complete your Deluno setup";
  const count = `${status.completedCount}/${status.totalCount} required steps complete`;

  return (
    <section
      aria-label="Setup progress"
      className="relative overflow-hidden rounded-2xl border border-hairline bg-card p-[var(--tile-pad)] shadow-card dark:border-white/[0.06]"
    >
      <div aria-hidden className="pointer-events-none absolute -right-20 -top-24 h-64 w-64 rounded-full bg-primary/10 blur-[80px]" />
      <div className="relative">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="flex items-center gap-2 text-[length:var(--type-caption)] font-bold uppercase tracking-[0.18em] text-primary">
              <Sparkles className="h-3.5 w-3.5" />
              Setup progress
            </p>
            <h2 className="mt-1 font-display text-[length:var(--type-title-sm)] font-semibold tracking-tight text-foreground">{heading}</h2>
            <p className="mt-1 max-w-3xl text-[length:var(--type-body-sm)] leading-relaxed text-muted-foreground">{status.summary}</p>
          </div>
          <span className="tabular shrink-0 rounded-full border border-hairline bg-surface-2 px-2.5 py-1 text-[length:var(--type-caption)] font-semibold text-muted-foreground">
            {count}
          </span>
        </div>

        <ol className="mt-5 grid gap-2 md:grid-cols-6 md:gap-0">
          {status.steps.map((step, index) => (
            <li key={step.id} className="relative min-w-0 md:flex md:flex-col md:items-center md:px-1">
              {index > 0 ? <span aria-hidden className="absolute left-0 right-1/2 top-5 hidden h-px bg-border md:block" /> : null}
              {index < status.steps.length - 1 ? <span aria-hidden className="absolute left-1/2 right-0 top-5 hidden h-px bg-border md:block" /> : null}
              <Link
                to={step.to}
                aria-label={`${step.number}. ${step.title}: ${stepStateLabel(step)}`}
                className="group relative flex min-w-0 items-start gap-3 rounded-xl border border-hairline bg-surface-1/70 p-3 transition hover:-translate-y-px hover:border-primary/35 hover:bg-primary/5 md:block md:w-full md:text-center dark:border-white/[0.06] dark:bg-white/[0.02]"
              >
                <StepStateIcon step={step} />
                <span className="min-w-0 md:mt-2 md:block">
                  <span className="block text-[length:var(--type-body-sm)] font-semibold text-foreground">{step.number}. {step.title}</span>
                  <span className={cn(
                    "mt-0.5 block text-[length:var(--type-caption)] leading-snug",
                    step.state === "failed" ? "text-destructive" : step.state === "complete" ? "text-success" : "text-muted-foreground"
                  )}>
                    {step.optional && step.state !== "complete" ? "Optional · " : ""}{stepStateLabel(step)}
                  </span>
                  <span className="mt-2 hidden items-center justify-center gap-1 text-[length:var(--type-micro)] font-bold uppercase tracking-[0.12em] text-primary md:inline-flex">
                    {step.action}
                    <ArrowRight className="h-3 w-3" />
                  </span>
                </span>
              </Link>
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}

function StepStateIcon({ step }: { step: SetupStatusStep }) {
  const Icon = step.state === "complete" ? CheckCircle2 : step.state === "failed" ? CircleX : Circle;
  const tone = step.state === "complete" ? "text-success" : step.state === "failed" ? "text-destructive" : "text-muted-foreground/50";
  return (
    <span className={cn("relative z-10 flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-card md:mx-auto", tone)}>
      <Icon className="h-7 w-7" strokeWidth={1.9} aria-hidden />
    </span>
  );
}

function stepStateLabel(step: SetupStatusStep) {
  if (step.state === "complete") return step.status;
  if (step.state === "failed") return `Needs attention · ${step.status}`;
  return step.status;
}
