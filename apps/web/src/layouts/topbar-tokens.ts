/**
 * The topbar row's three sizing constants.
 *
 * Pulled out of `app-layout.tsx` so a control that lives outside that file --
 * the notifications bell's own panel trigger, for one -- can still wear the
 * same height and icon size instead of guessing at a fourth pixel value.
 * Every control in the row uses these and none sets its own height or icon
 * size; that discipline is the whole fix #(see app-layout.tsx's topbar
 * header comment for the history).
 */
export const TOPBAR_CONTROL = "h-[var(--shell-pill-height)] shrink-0";
export const TOPBAR_ICON_BUTTON =
  "flex h-[var(--shell-pill-height)] w-[var(--shell-pill-height)] shrink-0 items-center justify-center rounded-xl text-muted-foreground transition hover:bg-muted/50 hover:text-foreground";
export const TOPBAR_ICON = "h-[var(--shell-icon-size)] w-[var(--shell-icon-size)]";
