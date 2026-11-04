import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Sheet, SheetContent } from "./sheet";

function open(props: Parameters<typeof SheetContent>[0] = {}) {
  render(
    <Sheet defaultOpen>
      <SheetContent aria-label="Heat" {...props}>Inside</SheetContent>
    </Sheet>
  );
  return screen.getByRole("dialog");
}

const overlay = () => document.querySelector<HTMLElement>("[data-state='open'].fixed.inset-0")!;

describe("a side panel's look", () => {
  it("dims the page softly behind a side panel, and darker behind a bottom one", () => {
    open();
    expect(overlay()).toHaveClass("fixed", "inset-0", "z-50", "bg-background/75", "backdrop-blur-[6px]");
  });

  it("dims the page with black behind a bottom panel", () => {
    open({ side: "bottom" });
    expect(overlay()).toHaveClass("fixed", "inset-0", "z-50", "bg-black/50", "backdrop-blur-[2px]");
    expect(overlay()).not.toHaveClass("bg-background/75");
  });

  it("is a full-height card against the edge, fading in", () => {
    expect(open()).toHaveClass("fixed", "top-0", "z-50", "h-full", "w-full", "max-w-md", "border-hairline", "bg-card", "shadow-lg", "animate-fade-in");
  });

  it("is a rounded floating card above the bottom edge of the phone, sliding in and out", () => {
    expect(open({ side: "bottom" })).toHaveClass(
      "bottom-[max(0.5rem,env(safe-area-inset-bottom))]", "max-h-[min(88dvh,640px)]", "dark:border-white/8", "dark:bg-[hsl(226_22%_9%/0.98)]",
      "data-[state=open]:animate-mobile-nav-sheet-in", "data-[state=closed]:animate-mobile-nav-sheet-out", "overflow-hidden", "shadow-2xl"
    );
  });

  it("gives the close button a quiet look with a visible focus ring", () => {
    open();
    expect(screen.getByRole("button", { name: "Close" })).toHaveClass("absolute", "rounded-md", "p-2", "text-muted-foreground", "transition", "hover:bg-secondary", "hover:text-foreground", "focus-visible:ring-2", "focus-visible:ring-primary/40");
  });

  it("takes the caller's own classes as well", () => {
    expect(open({ className: "extra-class" })).toHaveClass("extra-class");
  });
});
