import { Link } from "react-router-dom";
import { ArrowRight, CheckCircle2 } from "lucide-react";
import {
  advancedOperationPaths,
  operationPathById,
  quickOperationPaths,
  type OperationPath
} from "../../lib/operation-paths";
import { cn } from "../../lib/utils";
import { Badge } from "../ui/badge";
import { Button } from "../ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../ui/card";

interface OperationsGuideProps {
  compact?: boolean;
  className?: string;
}

export function OperationsGuide({ compact = false, className }: OperationsGuideProps) {
  return (
    <Card className={cn("settings-panel border-primary/20 bg-primary/5", className)}>
      <CardHeader>
        <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
          <div>
            <CardTitle>One clear place for each job</CardTitle>
            <CardDescription>
              Deluno keeps day-to-day work in quick paths and moves tuning into advanced paths so there are no duplicate operational screens.
            </CardDescription>
          </div>
          <Badge variant="info">Canonical workflow</Badge>
        </div>
      </CardHeader>
      <CardContent className={cn("grid gap-[var(--grid-gap)]", compact ? "xl:grid-cols-2" : "xl:grid-cols-[1.05fr_0.95fr]")}>
        <OperationColumn
          title="Quick actions"
          description="The places users should go first during normal daily use."
          paths={quickOperationPaths}
          featured
        />
        <OperationColumn
          title="Advanced mode"
          description="Power controls for routing, policy, API, and deep configuration."
          paths={advancedOperationPaths}
        />
      </CardContent>
    </Card>
  );
}

export function OperationPathBanner({
  actionTo,
  actionLabel,
  className,
  pathId
}: {
  actionTo?: string;
  actionLabel?: string;
  className?: string;
  pathId: string;
}) {
  const path = operationPathById(pathId);
  if (!path) return null;
  const Icon = path.icon;

  return (
    <div className={cn("rounded-2xl border border-hairline bg-card p-[calc(var(--tile-pad)*0.8)] shadow-card", className)}>
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex min-w-0 gap-3">
          <span className="flex h-[var(--control-height-icon)] w-[var(--control-height-icon)] shrink-0 items-center justify-center rounded-xl border border-primary/20 bg-primary/10 text-primary">
            <Icon className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" />
          </span>
          <span className="min-w-0">
            <span className="block font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
              Canonical path: {path.title}
            </span>
            <span className="mt-1 block text-dynamic-sm leading-relaxed text-muted-foreground">
              {path.description}
            </span>
          </span>
        </div>
        <Button asChild variant="outline" size="sm" className="shrink-0">
          <Link to={actionTo ?? path.to}>
            {actionLabel ?? "Open"}
            <ArrowRight className="h-4 w-4" />
          </Link>
        </Button>
      </div>
    </div>
  );
}

function OperationColumn({
  description,
  featured = false,
  paths,
  title
}: {
  description: string;
  featured?: boolean;
  paths: OperationPath[];
  title: string;
}) {
  return (
    <section className="min-w-0 rounded-2xl border border-hairline bg-card/75 p-[calc(var(--tile-pad)*0.8)]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h3 className="font-display text-[length:var(--type-card-title)] font-semibold tracking-tight text-foreground">
            {title}
          </h3>
          <p className="mt-1 text-dynamic-sm leading-relaxed text-muted-foreground">{description}</p>
        </div>
        {featured ? <CheckCircle2 className="h-5 w-5 shrink-0 text-primary" /> : null}
      </div>
      <div className="mt-[var(--grid-gap)] grid gap-2">
        {paths.map((path) => (
          <OperationLink key={path.id} path={path} />
        ))}
      </div>
    </section>
  );
}

function OperationLink({ path }: { path: OperationPath }) {
  const Icon = path.icon;
  return (
    <Link
      to={path.to}
      className="group grid gap-3 rounded-xl border border-hairline bg-surface-1 p-[calc(var(--tile-pad)*0.62)] transition-colors hover:border-primary/35 hover:bg-surface-2 sm:grid-cols-[auto_minmax(0,1fr)_auto] sm:items-center"
    >
      <span className="flex h-[var(--control-height-icon)] w-[var(--control-height-icon)] items-center justify-center rounded-xl border border-hairline bg-card text-muted-foreground transition-colors group-hover:border-primary/30 group-hover:text-primary">
        <Icon className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" />
      </span>
      <span className="min-w-0">
        <span className="block font-display text-[length:var(--type-body-lg)] font-semibold tracking-tight text-foreground">
          {path.title}
        </span>
        <span className="mt-0.5 block text-dynamic-sm leading-relaxed text-muted-foreground">
          {path.description}
        </span>
      </span>
      <Button asChild variant="ghost" size="sm" className="justify-self-start sm:justify-self-end">
        <span>
          Open
          <ArrowRight className="h-4 w-4" />
        </span>
      </Button>
    </Link>
  );
}
