import { ArrowRight, type LucideIcon } from "lucide-react";
import { Link } from "react-router-dom";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";

export function StudioOutcomeCard({
  icon: Icon,
  title,
  description,
  detail,
  to,
  action
}: {
  icon: LucideIcon;
  title: string;
  description: string;
  detail: string;
  to: string;
  action: string;
}) {
  return (
    <Card className="group flex min-h-56 flex-col transition hover:border-primary/35 hover:shadow-sm">
      <CardHeader>
        <span className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10 text-primary"><Icon className="h-5 w-5" /></span>
        <CardTitle className="pt-3">{title}</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent className="mt-auto">
        <p className="mb-3 text-xs font-medium text-muted-foreground">{detail}</p>
        <Link to={to} className="inline-flex items-center gap-1.5 text-sm font-semibold text-primary">
          {action}<ArrowRight className="h-4 w-4 transition group-hover:translate-x-0.5" />
        </Link>
      </CardContent>
    </Card>
  );
}
