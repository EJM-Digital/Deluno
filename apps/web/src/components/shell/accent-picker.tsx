import { Check, Palette } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import {
  ACCENTS,
  applyAccent,
  persistAccent,
  resolveInitialAccent,
  type AccentKey
} from "../../lib/theme-accent";
import { cn } from "../../lib/utils";

export function AccentPicker() {
  const [open, setOpen] = useState(false);
  const [accent, setAccent] = useState<AccentKey>(() => resolveInitialAccent());
  const panelRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  // Apply on mount + whenever state changes
  useEffect(() => {
    applyAccent(accent);
  }, [accent]);

  // Close on outside click / Escape
  useEffect(() => {
    if (!open) return;
    function onClick(e: MouseEvent) {
      if (
        panelRef.current?.contains(e.target as Node) ||
        triggerRef.current?.contains(e.target as Node)
      ) {
        return;
      }
      setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onClick);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onClick);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  function selectAccent(key: AccentKey) {
    setAccent(key);
    persistAccent(key);
  }

  return (
    <div className="relative">
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setOpen((prev) => !prev)}
        className={cn(
          "relative flex h-[var(--control-height-icon)] w-[var(--control-height-icon)] items-center justify-center rounded-lg text-muted-foreground transition hover:bg-muted/60 hover:text-foreground",
          open && "bg-muted/60 text-foreground"
        )}
        aria-label="Change accent color"
        aria-expanded={open}
        title="Accent color"
      >
        <Palette className="h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]" strokeWidth={1.75} />
        <span
          className="absolute bottom-1 right-1 h-2 w-2 rounded-full ring-2 ring-background transition-colors"
          style={{ backgroundColor: "hsl(var(--primary))" }}
        />
      </button>

      {open ? (
        <div
          ref={panelRef}
          role="dialog"
          aria-label="Accent color picker"
          className={cn(
            "absolute right-0 top-[calc(100%+8px)] z-50 w-[288px] origin-top-right overflow-hidden rounded-xl border border-hairline bg-popover shadow-lg",
            "backdrop-blur-2xl",
            "animate-fade-in"
          )}
        >
          <div className="border-b border-hairline px-4 py-3">
            <p className="text-dynamic-base font-semibold text-foreground">Accent color</p>
            <p className="mt-0.5 text-[length:var(--shell-subtle-size)] text-muted-foreground">
              Recolors buttons, highlights, and glow effects.
            </p>
          </div>

          <div className="grid max-h-[min(60vh,440px)] grid-cols-1 gap-0.5 overflow-y-auto p-2">
            {ACCENTS.map((def) => {
              const isActive = def.key === accent;
              return (
                <button
                  key={def.key}
                  type="button"
                  onClick={() => selectAccent(def.key)}
                  className={cn(
                    "group flex items-center gap-3 rounded-lg px-2 py-2 text-left transition",
                    "hover:bg-muted/60 dark:hover:bg-white/[0.05]",
                    isActive && "bg-muted/50 dark:bg-white/[0.04]"
                  )}
                >
                  {/* Swatch — dual-tone like the gradient */}
                  <div
                    className={cn(
                      "relative h-7 w-7 shrink-0 overflow-hidden rounded-lg ring-1 transition-[ring]",
                      isActive
                        ? "ring-2 ring-foreground"
                        : "ring-hairline group-hover:ring-muted-foreground/40"
                    )}
                  >
                    <div
                      className="absolute inset-0"
                      style={{
                        background: `linear-gradient(135deg, ${def.swatch.light}, ${def.swatch.dark})`
                      }}
                    />
                  </div>
                  <div className="min-w-0 flex-1">
                    <p
                      className={cn(
                        "truncate text-dynamic-base font-semibold",
                        isActive ? "text-foreground" : "text-foreground/85"
                      )}
                    >
                      {def.label}
                    </p>
                    <p className="truncate text-[length:var(--shell-subtle-size)] text-muted-foreground">
                      {def.description}
                    </p>
                  </div>
                  {isActive ? (
                    <Check className="h-4 w-4 shrink-0 text-primary" strokeWidth={2.25} />
                  ) : null}
                </button>
              );
            })}
          </div>

          <div className="border-t border-hairline px-4 py-2.5">
            <p className="text-[length:var(--shell-subtle-size)] text-muted-foreground">
              Preference is saved locally. Pair with dark/light mode for best contrast.
            </p>
          </div>
        </div>
      ) : null}
    </div>
  );
}
