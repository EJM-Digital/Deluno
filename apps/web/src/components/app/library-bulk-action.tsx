import type { ReactNode } from "react";
import { LoaderCircle } from "lucide-react";
import { cn } from "../../lib/utils";

export function BulkAction({
  label, icon, onClick, disabled, loading, variant = "ghost"
}: {
  label: string;
  icon: ReactNode;
  onClick: () => void;
  disabled?: boolean;
  loading?: boolean;
  variant?: "ghost" | "primary" | "danger";
}) {
  return <button type="button" onClick={onClick} disabled={disabled} aria-label={label} className={cn(
    "flex min-h-[var(--library-toolbar-height)] items-center gap-1.5 rounded-xl px-3 text-[length:var(--library-toolbar-size)] font-medium transition-all duration-150 select-none",
    "disabled:opacity-40 disabled:cursor-not-allowed",
    variant === "primary" ? "bg-gradient-to-br from-primary to-[hsl(var(--primary-2))] text-primary-foreground shadow-[0_2px_8px_hsl(var(--primary-deep)/0.4),inset_0_1px_0_hsl(0_0%_100%/0.15)] hover:brightness-110 active:scale-95" :
      variant === "danger" ? "text-destructive hover:bg-destructive/10 hover:text-destructive active:bg-destructive/15" :
        "text-[hsl(var(--media-muted-foreground))] hover:bg-white/[0.07] hover:text-[hsl(var(--media-foreground))] active:bg-white/[0.04]"
  )}>
    {loading ? <LoaderCircle className="h-3.5 w-3.5 animate-spin" /> : icon}
    <span className="hidden sm:inline">{label}</span>
  </button>;
}
